# 第三方说明

微信 4.x 数据库配置布局、固定掩码及消息结构的研究与实现参考了 [chenanhan230-del/WeChatMsg-4x](https://github.com/chenanhan230-del/WeChatMsg-4x)，尤其是 `wx4/key.py`、`wx4/common.py`、`wx4/decrypt.py`、`wx4/reader.py`。原项目采用 GNU GPL v3。本项目按 GPL-3.0-or-later 提供，完整许可证在 `LICENSE` 中；交付包附对应源码。WAL 校验与仅应用已提交帧的处理参考 [SQLite 文件格式文档](https://www.sqlite.org/fileformat2.html#walformat)。

NuGet 依赖：

- Microsoft.Data.Sqlite 10.0.11、SQLitePCLRaw：各自的 MIT / Apache-2.0 许可证及 SQLite public domain 条款，详见其 NuGet 包。
- [ZstdSharp.Port 0.8.8](https://github.com/oleg-st/ZstdSharp)：MIT，Copyright (c) 2021 Oleg Stepanischev。下附许可证。
- 独立 EXE 版本包含 .NET 和 Windows Desktop 运行环境，其对应许可证及第三方说明随包附在 `licenses/` 下。

语音解码使用 Skype SILK SDK 1.0.9（Copyright (c) 2006–2012 Skype Limited），采用 SDK 各源文件中的 BSD 风格许可。源码来自 [DrAbcOfficial/SilkSharp 提交 3dbfc2bd](https://github.com/DrAbcOfficial/SilkSharp/tree/3dbfc2bd1b090094e221187c3a9d25583fa11916/silkcodec) 的 SDK 部分，保存在 `native/silk/`，保留各文件原有版权及许可。未使用其文件转换包装器或 NuGet 运行库。本项目的 `native/decoder.c` 内存解码包装器按 GPL-3.0-or-later 提供。发布包附完整构建源码 `source/native/`，SDK 许可另附在 `licenses/SILK-SDK.txt`；仓库副本为 `native/SILK-LICENSE.txt`。

语音测试样本是本项目生成的双频合成音，不含录音、对话或个人数据；参考结果由独立 SDK 解码得到。

## ZstdSharp MIT License

Copyright (c) 2021 Oleg Stepanischev

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
