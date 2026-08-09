# AnimeGoNetData

AnimeGoNetData 是 `wetor/AnimeGoData` 的 .NET 10 / NativeAOT 替代实现，用于给 AnimeGoNet 生成 `DATA_MANIFEST_V1` 数据发布产物。旧项目输出 BoltDB；本项目输出可在线下载、可离线导入的 `manifest.json`、独立 `JSONL.gz` 分片、`SHA256SUMS` 和 offline ZIP。

## 数据源

默认读取 Bangumi Archive 最新 Release API：

<https://api.github.com/repos/bangumi/Archive/releases/latest>

程序会严格从 Release assets 中选择 `updated_at` 最新的 `.zip` 文件，不选择 `.7z`。ZIP 内必须包含根级：

- `subject.jsonlines`
- `episode.jsonlines`

GitHub API 请求会读取 `GITHUB_TOKEN` 或 `GH_TOKEN`，用于提高限流额度。

## DATA_MANIFEST_V1

`manifest.json` 严格匹配 AnimeGoNet 主程序的 `DATA_MANIFEST_V1`：

- `schema_version = 1`
- `data_version`
- `generated_at_utc`
- `minimum_client_version`
- `upstream.repository / release / asset / sha256`
- `assets[]`：`kind`、`file_name`、`url`、`size_bytes`、`sha256`、`record_count`、`subject_id_min`、`subject_id_max`
- `totals.subjects / totals.episodes`

所有 SHA-256 均为 64 位小写十六进制。manifest 中的文件大小、hash、记录数和 subject ID 范围来自真实生成资产。

## 数据内容

Subject JSONL 每行字段：

```json
{"id":51,"name":"CLANNAD","name_cn":"CLANNAD","air_date":"2007-10-05","episode_count":23}
```

Episode JSONL 每行字段：

```json
{"id":1423,"subject_id":51,"sort":1,"episode":"1","air_date":"2007-10-05"}
```

清洗规则：

- subject 只保留 `type = 2` 的动画条目。
- episode 只保留 `type = 0` 且引用到保留 subject 的普通集。
- subject 按 `id` 升序输出；episode 在每个分片内按 `id` 升序输出。
- `episode_count` 来自该 subject 的普通集数量。
- `air_date` 来自 subject `date` 或 episode `airdate`，无效日期输出 `null`。
- 重复 subject/episode ID、坏 JSON、缺少 ZIP entry 会使发布失败；不能满足输出协议的 type=0 Episode（例如缺失或非正数 `sort`）会被跳过。

## 输出目录

生成目录包含：

- `manifest.json`
- `bangumi-subjects-v1-<min>-<max>.jsonl.gz`
- `bangumi-episodes-v1-<min>-<max>.jsonl.gz`
- `SHA256SUMS`
- `animegonetdata-<data_version>-offline.zip`

offline ZIP 根目录只包含 `manifest.json` 和 manifest 声明的全部 JSONL.gz 分片，不包含目录、额外文件、重复文件或 tar 嵌套。ZIP 内分片字节与在线 Release 独立资产完全一致。

## CLI

使用本地 ZIP：

```bash
dotnet run --project src/AnimeGoNetData.Cli -- \
  --zip /path/to/dump.zip \
  --output out/animegonetdata-2026.08.04.1 \
  --data-version 2026.08.04.1 \
  --asset-base-url https://github.com/DeQxJ00/AnimeGoNetData/releases/download/2026.08.04.1/ \
  --upstream-release archive \
  --minimum-client-version 0.1.0 \
  --subjects-per-shard 25000 \
  --min-subjects 30000 \
  --min-episodes 300000
```

只验证 Release 资产选择，不下载全量 ZIP：

```bash
dotnet run --project src/AnimeGoNetData.Cli -- --release-api https://api.github.com/repos/bangumi/Archive/releases/latest --select-asset-only
```

## GitHub Actions

`.github/workflows/archive.yml` 与原 AnimeGoData 保持相同定时：

- `cron: "0 0 * * 3"`，每周三 UTC 00:00，北京时间 08:00
- 支持任意 tag push
- 支持 `workflow_dispatch`

该 workflow 会 restore/build/test，发布 `linux-x64` NativeAOT 生成器，下载 Bangumi Archive 最新 ZIP，用原生二进制生成全量 `DATA_MANIFEST_V1` 产物，然后创建或校验不可变 `data_version` Release。Release assets 包含 `manifest.json`、每个 manifest 声明的独立 JSONL.gz 分片、`SHA256SUMS` 和 offline ZIP。全部资产名和字节校验通过后，才将该 Release 标记为 latest。

`.github/workflows/ci.yml` 提供普通 CI 和 linux-x64 NativeAOT smoke。

## AnimeGoNet 导入建议

AnimeGoNet 在线更新应使用 GitHub latest manifest URL：

```text
https://github.com/DeQxJ00/AnimeGoNetData/releases/latest/download/manifest.json
```

主程序读取 manifest 后按 `assets[].url` 下载每个独立 JSONL.gz 分片，并校验 `size_bytes`、`sha256`、记录数、subject ID 范围和 totals。离线导入使用 `animegonetdata-<data_version>-offline.zip`，其根目录结构与 manifest 声明严格一致。
