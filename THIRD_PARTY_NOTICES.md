# 第三方说明

微信 4.x 数据库配置布局、固定掩码及消息结构的研究与实现参考了 [chenanhan230-del/WeChatMsg-4x](https://github.com/chenanhan230-del/WeChatMsg-4x)，尤其是 `wx4/key.py`、`wx4/common.py`、`wx4/decrypt.py`、`wx4/reader.py`。原项目采用 GNU GPL v3。本项目按 GPL-3.0-or-later 提供，完整许可证在 `LICENSE` 中；交付包附对应源码。WAL 校验与仅应用已提交帧的处理参考 [SQLite 文件格式文档](https://www.sqlite.org/fileformat2.html#walformat)。

NuGet 依赖：

- Microsoft.Data.Sqlite 10.0.11、SQLitePCLRaw：各自的 MIT / Apache-2.0 许可证及 SQLite public domain 条款，详见其 NuGet 包。
- [ZstdSharp.Port 0.8.8](https://github.com/oleg-st/ZstdSharp)：MIT，Copyright (c) 2021 Oleg Stepanischev。下附许可证。

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
