# SILK 解码组件

`silk/src/` 和 `silk/interface/` 保留 Skype SILK SDK 1.0.9 的 C 源码和原有版权声明，取自 [DrAbcOfficial/SilkSharp](https://github.com/DrAbcOfficial/SilkSharp/tree/3dbfc2bd1b090094e221187c3a9d25583fa11916/silkcodec) 的 `libsilk/` 和 `interface/`，上游提交 `3dbfc2bd1b090094e221187c3a9d25583fa11916`。仅使用这些 SDK 源文件，不使用上游文件转换包装器。SDK 为 BSD 风格许可，见各源文件及 `SILK-LICENSE.txt`。

`decoder.c` 是本项目的内存解码包装器，按项目 GPL-3.0-or-later 提供。它校验帧边界、解码状态及输出容量，不接受音频文件路径。输出为 24 kHz、单声道、16 位 PCM；WAV 容器由 C# 写入。

项目构建时自动运行 `build.ps1`，需要 Visual Studio 2022 或更新版本的 C++ 桌面开发工作负载。使用 x64 MSVC 和静态 C 运行库，无需用户另装 Visual C++ 运行库。生成物在应用项目的 `obj/native/` 中，DLL 作为资源打包进程序集，首次解码时按内容哈希释放并加载。构建及发布包包含本目录完整源码；无需构建时联网下载 SDK。
