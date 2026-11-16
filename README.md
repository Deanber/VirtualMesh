# Virtual Mesh

将静态模型预处理为可复用的几何簇资产，在 Unity URP 中使用 GPU LOD、视锥/Hi-Z 遮挡剔除、间接绘制和共享几何驻留。支持 Scene 编辑态预览和 Play。

这是基于 Unity Virtual Mesh 示例扩展的开发版本。仓库根目录就是 UPM 包源码，按无 `.meta` 的嵌入式包方式使用。

## 环境

- Unity **6000.3**，当前开发验证版本 **6000.3.21f1**。
- URP **17.3.0**、Shader Graph **17.3.0**、Mathematics **1.3.3**，由 `package.json` 声明。
- 当前附带的烘焙原生库是 **Windows x64 Editor** 的 `meshoptimizer.dll`；没有附带 macOS/Linux 烘焙库。
- 渲染主要验证于 Windows / Direct3D 12。其它图形后端与 Player 配置需另行验证。

## 安装

将仓库克隆或解压到目标 Unity 项目的 `Packages/com.unity.virtualmesh`，打开项目后由 Unity 导入嵌入式包并解析依赖。不要把仓库放进 `Assets/`，shader include 使用固定的包路径。

本分发没有 `.meta`，不作为直接通过 Package Manager 的 **Git URL** 安装的完整不可变发布包。Unity 的[包目录规范](https://docs.unity3d.com/6000.0/Documentation/Manual/cus-layout.html)要求被导入的资源具备元数据；在可写的嵌入式目录中让 Unity 本地生成即可，`.gitignore` 会阻止它们进入 Git。

本仓库按要求不提交 `.meta`，程序集使用名称引用。Unity 首次导入会生成本地元数据；包内限定路径的导入脚本会将烘焙 DLL 设置为仅 Windows x64 Editor。此分发不保留原工程的资产 GUID，旧场景、材质和 Prefab 不能依靠原 GUID 自动迁移；请在目标工程重新创建配置、烘焙模型并检查引用。包名 `com.unity.virtualmesh` 用于 shader include 路径，请勿单独改名。

## 快速使用

1. 使用 **Forward+** Universal Renderer，启用 Render Graph，关闭 MSAA。
2. 在该 Renderer Data 添加并启用 **Virtual Mesh Model Render Feature**，让目标相机使用这个 Renderer。
3. 在 Project 中选择静态 FBX、Prefab 或已保存的 Mesh，执行 **Assets > Virtual Mesh > Bake Model Clusters**。
4. 将发布出的 Prefab 放入场景。输出位于 `Assets/VirtualMeshModels/`；首次 Bake 后，源资产重导入会自动更新。
5. 在 **Tools > Virtual Mesh > Rendering Settings** 调整配置；通过实例的 Diagnostic 和 `VirtualMeshRuntime.Statistics` 查看驻留和回退状态。

通用路径支持 URP Lit/Unlit。任意自定义材质需要适配，不能仅换 Renderer Feature 就获得相同外观。


## 当前能力与边界

- 已包含两阶段 Hi-Z、可选 4×4 Hi-Z、受支持材质的 MaterialDepth/resolve、Scene motion 依赖修复、可选三流顶点/GPU 转置，以及 Editor 会话内跨 Play 准备缓存。
- 森林工程中的开关属于原场景配置，本包不会自动复制这些配置，也不保证固定帧率。
- 几何仍按整模型共享 atlas 驻留；新增模型或模型版本改变仍可能重建整个 atlas，准备仍依赖渲染回调。
- 会话缓存只复用同一 Editor 会话中完全匹配且准备完成的几何；首次打开 Editor、代码/模型变化或缓存不受支持时仍需冷准备，切换时仍有 GPU 复制成本。
- 增量资源驻留、独立编辑态准备和按页流送尚未实现。保留 Forward+，未强制 Deferred。

## 文档与来源

本次整理通过 Runtime（Editor/Player 条件）与 Editor 三项离线 C# 编译，并检查了 114 处 shader include 路径。没有在全新 Unity 项目中重跑导入、烘焙、shader 编译或性能测试。

源自 [Unity Virtual Mesh](https://github.com/Unity-Technologies/com.unity.virtualmesh) 示例。保留原始 [Unity Companion License 声明](LICENSE.md) 与[第三方声明](Third%20Party%20Notices.md)；不是 Unity 官方的新发布版本。
