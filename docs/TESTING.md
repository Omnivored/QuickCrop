# 构建、测试与演示生成

返回 [首页](../README.md)。

## 环境

- Windows 10/11 x64。
- .NET 10 SDK。运行轻量版只需要 .NET 10 Desktop Runtime x64，不需要 SDK。
- 构建首次还原 NuGet 包时需要联网；图片查看、裁剪和保存本身不需要网络。
- UI 测试需要可交互的 Windows 桌面会话，不适用于无桌面的 Linux 环境。

## 开发运行

在仓库根目录执行：

```powershell
dotnet restore QuickCrop.csproj --locked-mode
dotnet run --project QuickCrop.csproj
```

项目基于 WPF，图片处理使用 Magick.NET-Q8-x64。常见格式预览优先走 Windows 原生解码，其余使用内置解码后备路径。

## 回归测试

```powershell
dotnet run --project tests -c Release -p:BaseOutputPath=bin/verify/ -- --ui --recycle
```

1.3.1 发布前结果：**117 passed, 0 failed**。这代表当前样例和断言通过，不代表任意硬件、损坏图片或断电场景均可保证成功。

覆盖范围包括：

- 裁剪几何边界、比例、最大选框与随机拖动。
- 常见格式、HEIC 后备解码、EXIF 方向、透明度与首帧处理。
- 缓存、预读、取消过期请求、文件变化失效、缩略图内存隔离。
- 原分辨率导出、精确像素尺寸、原图字节不变、冲突编号与失败回退。
- 无中间弹窗裁剪入口、目录设置草稿、快捷键与逐图旋转记忆。
- 五个前台模板、第六格下拉、命名模板、位置互换与固定规格批次。
- 滚轮定位缩放、高清预览变换保留、松开鼠标自动预览、返回和撤销。
- 成品原位替换、缩略图更新、跳过已完成项、外部输出目录刷新及重复裁剪。
- 回收站删除、占用失败保护和拒绝永久删除回退。

测试使用独立临时目录和设置。`--recycle` 会把测试新生成的图片送入系统回收站，不会操作用户原图。截图输出到 `output/qa`，该目录不进入版本控制。

## 生成演示素材

```powershell
dotnet run --project tests -c Release -p:BaseOutputPath=bin/verify/ -- --demo
```

使用 `samples/01-lake.jpg` 及其程序生成的变体，在独立临时目录中回放真实界面状态，生成 `docs/images/` 下的截图和三段 GIF。此过程不会读取真实用户图片或修改用户设置。

演示还会校验像素模板确实导出为 1080 × 1920、保存成品确实替代原列表项、动图确实包含多帧。文档路径会中性化处理。GIF 的帧间隔是为了阅读人为设置，不是实时性能测量。

## 构建发布包

```powershell
.\build.ps1
.\build.ps1 -Standalone
```

输出位置分别为：

```text
dist/QuickCrop-1.3.1-Light/QuickCrop.exe
dist/QuickCrop-1.3.1-Portable/QuickCrop.exe
```

轻量版依赖桌面运行时，Portable 自带运行环境。分发时保留 README、docs 和 LICENSES 文件夹。源代码和发布包分离：`dist/` 不提交到 Git，压缩包通过 GitHub Releases 分发。

## 可选性能样例

```powershell
dotnet run --project tests -c Release -p:BaseOutputPath=bin/verify/ -- --perf
```

基于样例生成大图，比较固定读取路径的预览准备时间。它不等同于完整窗口响应时间，也不能直接作为与 WPS 或其他查看器的性能比较。不同编码、磁盘、内存和系统缓存都会影响结果。
