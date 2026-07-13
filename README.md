# AnimeGoNetData

AnimeGoNetData 是 `wetor/AnimeGoData` 的 .NET 10 替代实现，用于给 AnimeGoNet 生成可流式导入的数据包。旧项目输出 BoltDB；本项目输出版本化 `JSONL.gz` 分片和 `manifest.json`。

## 数据源

默认读取 Bangumi Archive 最新 Release API：

<https://api.github.com/repos/bangumi/Archive/releases/latest>

程序会严格从 Release assets 中选择 `updated_at` 最新的 `.zip` 文件，不选择 `.7z`。ZIP 内必须包含：

- `subject.jsonlines`
- `episode.jsonlines`

GitHub API 请求会读取 `GITHUB_TOKEN` 或 `GH_TOKEN`，用于提高限流额度。

## 清洗语义

- subject 只保留 `type = 2` 的动画条目。
- episode 只保留 `type = 0` 的普通集。
- 输出 subject 字段：`id`、`name_cn`、`name`、`eps`、`airdate`、`type`。
- 输出 episode 字段：`id`、`subject_id`、`sort`、`name`、`name_cn`、`type`、`airdate`。
- 输入允许乱序；输出按 subject `id` 和 episode `subject_id/sort/id` 排序。
- 重复 ID 会用规范 JSON 字符串较小的一条作为确定性取值。
- subject 的 `eps` 来自该 subject 的普通集数量，`airdate` 来自排序后第一条非空普通集 `airdate`。
- 损坏 JSON 行会跳过并计数；缺少 ZIP entry 会失败。

## 输出格式

输出目录包含：

- `bangumi-subjects-v1-00000.jsonl.gz`
- `bangumi-episodes-v1-00000.jsonl.gz`
- `manifest.json`

分片文件是 UTF-8 JSON Lines 后再 gzip 压缩，适合逐行流式处理。`manifest.json` 最后写入，包含：

- `schema_version`
- `dataset_version`
- `generated_at`
- 上游 asset 名称、URL、更新时间、大小
- subject/episode 记录数
- 每个输出文件的字节数和 SHA-256

生成过程先写 staging 目录，所有分片和 manifest 成功后才发布到输出目录。

## CLI

```powershell
E:\WorkSpaceAI\.dotnet10\dotnet.exe run --project src\AnimeGoNetData.Cli -- `
  --output out `
  --release-api https://api.github.com/repos/bangumi/Archive/releases/latest `
  --chunk-size 100000 `
  --min-subjects 1000 `
  --min-episodes 10000
```

使用本地或远程 ZIP：

```powershell
E:\WorkSpaceAI\.dotnet10\dotnet.exe run --project src\AnimeGoNetData.Cli -- --output out --zip E:\path\dump.zip
```

只验证 Release 资产选择，不下载 400MB ZIP：

```powershell
E:\WorkSpaceAI\.dotnet10\dotnet.exe run --project src\AnimeGoNetData.Cli -- --release-api https://api.github.com/repos/bangumi/Archive/releases/latest --select-asset-only
```

## GitHub Actions

`.github/workflows/archive.yml` 与原 AnimeGoData 保持相同定时：

- `cron: "0 0 * * 3"`，每周三 UTC 00:00，北京时间 08:00
- 支持任意 tag push
- 支持 `workflow_dispatch`

该 workflow 会 restore/build/test，发布 `linux-x64` NativeAOT 生成器，用原生二进制生成全量数据，并更新 rolling Release tag `archive`。Release assets 包含数据包、manifest 和 checksums。

`.github/workflows/ci.yml` 提供 Windows/Linux 普通 CI 和 linux-x64 NativeAOT smoke。

## AnimeGoNet 导入建议

AnimeGoNet 导入 SQLite 时建议直接流式读取 `JSONL.gz`：

1. 打开 gzip 流。
2. 用 `StreamReader.ReadLineAsync` 逐行读取。
3. 使用 `System.Text.Json` source generation 反序列化固定结构。
4. 用 SQLite transaction 批量插入，例如每 5,000 到 20,000 行提交一次。
5. 先导入 episodes，再导入 subjects，或使用 manifest 中的记录数做完整性校验。
