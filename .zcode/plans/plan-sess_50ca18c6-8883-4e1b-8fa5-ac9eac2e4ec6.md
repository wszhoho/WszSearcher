## 全文索引性能优化

### 第一步：快速低风险优化
1. 全量重建时跳过 DeleteDocuments（已有 DeleteAll）
2. Commit 间隔 200→5000（实际只在末尾 commit 一次）
3. 拼音计算跳过不含中文的文本

### 第二步：并行化
1. 枚举文件路径到 List（5 万条约 5MB）
2. Parallel.ForEach 并行处理（MaxDegreeOfParallelism=4）
3. Commit 只在末尾一次

预期：5 万文件从 ~40 分钟降到 5-8 分钟。