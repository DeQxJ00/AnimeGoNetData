# AnimeGoNetData

AnimeGoNetData 是 `wetor/AnimeGoData` 的 .NET 10 / NativeAOT 替代实现，用于给 AnimeGoNet 生成 AnimeGoNet `DATA_MANIFEST_V2` 数据发布产物。旧项目输出 BoltDB；本项目输出可在线下载、可离线导入的 `manifest.json`、独立 `JSONL.gz` 分片、`SHA256SUMS` 和 offline ZIP。

## 数据源

默认读取 Bangumi Archive 最新 Release API：

<https://api.github.com/repos/bangumi/Archive/releases/latest>

程序会严格从 Release assets 中选择 `updated_at` 最新的 `.zip` 文件，不选择 `.7z`。ZIP 内必须包含根级：

- `subject.jsonlines`
- `episode.jsonlines`
- `subject-relations.jsonlines`

GitHub API 请求会读取 `GITHUB_TOKEN` 或 `GH_TOKEN`，用于提高限流额度。

## DATA_MANIFEST_V2

`manifest.json` 严格匹配 AnimeGoNet 主程序的 `DATA_MANIFEST_V2`：

- `schema_version = 2`
- `data_version`
- `generated_at_utc`
- `minimum_client_version`
- `upstream.repository / release / asset / sha256`
- `assets[]`：`kind`（`subjects`、`episodes` 或 `relations`）、`file_name`、`url`、`size_bytes`、`sha256`、`record_count`、`subject_id_min`、`subject_id_max`
- `totals.subjects / totals.episodes / totals.relations`

schema v1（只有 subjects/episodes）仍由主程序兼容读取；本仓库新发布版本使用 schema v2，且必须包含三类资产。

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

Relations JSONL 每行字段：

```json
{"subject_id":51,"related_subject_id":42,"relation_type":2,"order":0}
```

清洗规则：

- subject 只保留 `type = 2` 的动画条目。
- episode 只保留 `type = 0` 且引用到保留 subject 的普通集。
- subject 按 `id` 升序输出；episode 在每个分片内按 `id` 升序输出。
- `episode_count` 来自该 subject 的普通集数量。
- `air_date` 来自 subject `date` 或 episode `airdate`，无效日期输出 `null`。
- 重复 subject/episode ID、坏 JSON、缺少 ZIP entry 会使发布失败；不能满足输出协议的 type=0 Episode（例如缺失或非正数 `sort`）会被跳过。
- relations 只保留 source/target 都是已保留 `type = 2` 动画 Subject 的记录；非动画目标会被过滤，不能产生悬空引用。
- relation 保留 Bangumi Archive 原始正整数 `relation_type`；输出按 `subject_id`、`order`、`related_subject_id`、`relation_type` 排序，同一三元组重复会使发布失败。

## 业务边界

当前 v2 发布 Bangumi Subject 基础字段、普通 Episode 和 Subject relations；不包含季度判断结果或完整 AI 作品参考数据。

- 普通季度匹配：`name`、`name_cn`、`air_date` 只能作为 TMDB Series/Season 的搜索候选证据；最终 Series/Season 必须调用并验证 TMDB。
- P3 Backtrace：配套 v2 客户端可使用离线 relations；v1 active 数据、无 active 数据或 Subject 不存在时，主程序 `GetRelatedSubjectsAsync` 才通过在线 Bangumi `/v0/subjects/{id}/subjects` 回退获取 relations。
- AI：主程序请求只发送作品级 `bgmid`，以及可选的、由 Bangumi Episode 日期计算出的 `bgm_episode_candidate`；不会把离线 Subject 详情直接发送给模型。详细 Bangumi 参考由 Bangumi MCP 按需取得，最终仍由 TMDB 验证。

本仓库只负责发布 relations 数据；离线 P3 仍要求 AnimeGoNet v2 主程序同步实现 manifest/schema、SQLite、store 和 client 的 relations 读取。

## 输出目录

生成目录包含：

- `manifest.json`
- `bangumi-subjects-v1-<min>-<max>.jsonl.gz`
- `bangumi-episodes-v1-<min>-<max>.jsonl.gz`
- `relations-0001.jsonl.gz`（必要时可扩展为多个 relations 分片）
- `SHA256SUMS`
- `animegonetdata-<data_version>-offline.zip`

offline ZIP 根目录只包含 `manifest.json` 和 manifest 声明的全部 JSONL.gz 分片，不包含目录、额外文件、重复文件或 tar 嵌套。ZIP 内分片字节与在线 Release 独立资产完全一致。

## CLI

使用本地 ZIP：

```bash
dotnet run --project src/AnimeGoNetData.Cli -- \
  --zip /path/to/dump.zip \
  --output out/animegonetdata-2026.08.04.2 \
  --data-version 2026.08.04.2 \
  --asset-base-url https://github.com/DeQxJ00/AnimeGoNetData/releases/download/2026.08.04.2/ \
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

该 workflow 会 restore/build/test，发布 `linux-x64` NativeAOT 生成器，下载 Bangumi Archive 最新 ZIP，用原生二进制生成全量 `DATA_MANIFEST_V2` 产物，然后创建或校验不可变 `data_version` Release。Release assets 包含 `manifest.json`、每个 manifest 声明的独立 JSONL.gz 分片、`SHA256SUMS` 和 offline ZIP。全部资产名和字节校验通过后，才将该 Release 标记为 latest。

`.github/workflows/ci.yml` 提供普通 CI 和 linux-x64 NativeAOT smoke。

## AnimeGoNet 导入建议

AnimeGoNet 在线更新应使用 GitHub latest manifest URL：

```text
https://github.com/DeQxJ00/AnimeGoNetData/releases/latest/download/manifest.json
```

主程序读取 manifest 后按 `assets[].url` 下载每个独立 JSONL.gz 分片，并校验 `size_bytes`、`sha256`、记录数、subject ID 范围和 totals。离线导入使用 `animegonetdata-<data_version>-offline.zip`，其根目录结构与 manifest 声明严格一致。

在配套的 AnimeGoNet v2 客户端中，active schema v2 的 relations 可供 `GetRelatedSubjectsAsync` 优先零网络读取；当前 Subject 没有关系时返回权威空列表。无 active 数据、v1 active 数据或 Subject 不存在时，客户端才回退在线 Bangumi。`relation_type = 2` 表示 Bangumi 原始“前传”关系，主程序据此执行 P3 Backtrace。

季度匹配仍只把 Subject 的 `name`、`name_cn`、`air_date` 作为 TMDB Series/Season 搜索候选证据，最终必须调用并验证 TMDB。AI 请求只发送作品级 `bgmid` 和可选的、由 Episode 日期计算出的 `bgm_episode_candidate`；不会把离线 Subject 详情直接发送给模型，详细 Bangumi 参考由 Bangumi MCP 按需取得并最终由 TMDB 验证。
