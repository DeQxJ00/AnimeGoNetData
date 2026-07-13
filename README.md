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

