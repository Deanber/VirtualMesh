# ElectricDreams 材质适配示例

从 Package Manager 的 Samples 导入后，`Shaders/` 内的相对目录结构须保留。也可把整个示例目录复制到项目 `Assets/` 下。它依赖已安装的 `com.unity.virtualmesh`、URP 和 Core 包。

包含 Foliage、Hornbeam、Surface 三种源 shader 及对应 Virtual Mesh shader；Foliage/Hornbeam 包含森林 MaterialDepth/resolve 路径。源材质使用 `ElectricDreams/Source ...`，模型烘焙时选择对应 `Virtual Mesh/ElectricDreams ...` 适配。通用 URP Lit/Unlit 不需要这个示例。

本示例不附带 Electric Dreams 原始内容、模型、纹理、材质、场景、导入计划或批量工具。请按 shader Properties 提供匹配材质输入。源码注释中对原开发工程 `ArtSource/...` 的引用仅是历史说明，不是安装依赖；本示例不能独立重现原森林或保证对任意材质等画质。

已有同名 source/VM shader 时请复用现有适配器，不重复导入。发布模型之后，生成材质会引用所使用的 shader。
