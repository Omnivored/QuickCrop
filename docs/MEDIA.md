# 演示素材与第三方说明

## 演示照片

`samples/01-lake.jpg` 是用于本地验证和文档演示的 Unsplash 样例照片，原始来源：

https://images.unsplash.com/photo-1470770841072-f978cf4d019e

照片适用 [Unsplash License](https://unsplash.com/license)。图片权利属于原作者，不是 QuickCrop 自行拍摄或生成的照片。文档中的竖图、翻转图和裁剪图由此样例处理得到，截图仅用于展示程序界面。图片不表示作者或 Unsplash 对本项目的认可。

`docs/images/` 为实际程序界面截图和基于截图生成的 GIF，使用独立示例目录和样例图片，不包含个人照片。动图为分步状态回放，不是实时录屏。

## HEIC 测试样例

`tests/fixtures/example.heic` 来自 [strukturag/libheif](https://github.com/strukturag/libheif) 的 `examples/example.heic`：

https://github.com/strukturag/libheif/blob/master/examples/example.heic

仅用于输入格式解码回归，未纳入 EXE 发布包。该文件及其上游权利不由 QuickCrop 项目重新授权；请参考上游仓库的许可与声明。

## 程序组件

- [Magick.NET](https://github.com/dlemstra/Magick.NET)：Apache-2.0，图片解码与导出。
- [ImageMagick](https://imagemagick.org/) 及其内置 delegates：详细版权、许可和使用条件见 `LICENSES/Magick.NET-and-delegates-NOTICE.txt`。
- .NET / WPF：Microsoft，相关许可及第三方声明见 `LICENSES/dotnet-LICENSE.txt`、`LICENSES/dotnet-THIRD-PARTY-NOTICES.txt`。

项目自身尚未指定开源许可证。第三方组件和媒体仍遵循各自原许可，不因本仓库公开而变更。
