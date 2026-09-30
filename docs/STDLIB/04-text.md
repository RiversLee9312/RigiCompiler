# 各 NS 的最小能力：core.text（§4.3）

> 本文件是 [STDLIB.md](../STDLIB.md)（Rigi 标准库 MVP 设计）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

### 4.3 `core.text`

MVP 提供字符串切片、查找、前后缀判断、替换、分割、拼接、裁剪、Unicode 大小写映射与标量遍历、`StringBuilder`、UTF-8 增量编解码、整数和浮点解析。以下公共行为已确定。

#### 4.3.1 字符、字符串与位置

- `char` 使用 32 位承载一个 [Unicode 标量](https://www.unicode.org/glossary/#unicode_scalar_value)：U+0000–U+10FFFF，排除 U+D800–U+DFFF。`'😀'` 是合法单个 char，组合字符序列仍不构成单个 char。整数转 char 时检查合法范围，不截断或回绕。
- 此裁决明确调整现有 16 位 char 表示。实现时同步字面量、转换、VM、native、Span 布局、序列化和语言/BIL/运行时规范；不能仅改标准库声明。§4.4 的 `readChar` 和 §4.7 的 char 映射统一使用此标量语义。
- String 保持不可变值语义；`String.length: i64` 是 UTF-8 字节数，`characterCount: i64` 是标量数。标量不等于用户感知的字素簇；组合重音、ZWJ 和区域指示符分别计数。U+0000 是普通内容，不作为字符串终止符。
- `slice`、`indexOf`、`lastIndexOf` 的位置采用零基 i64 UTF-8 字节偏移，与 length 一致。按标量操作使用明确的 `sliceCharacters` / `characterAt`；首版不新增 `String[index]` 访问。
- `slice(start, count)` 返回字节范围 `[start, start + count)` 的 String，`slice(start)` 取到末尾；两端必须是 UTF-8 标量边界。`sliceCharacters(start, count)` 按标量计数。负参数、越界、求和溢出或切断编码序列均抛 `core.OutOfBoundException`，不自动调整范围或边界；合法边界上的零长度返回空串，末尾也是合法边界。
- `characterAt(index: i64): char?` 按标量索引，越界返回 null。`characters()` 使用既有 IEnumerable/IEnumerator 协议，每次遍历有独立游标。按标量随机定位不承诺 O(1)，顺序处理优先遍历。
- UTF-8 导出和原始字节切片返回独立 `Span\<u8>`，可以截取任意字节范围；修改缓冲区不影响原 String，从字节重建 String 必须经解码。String 位置保持 i64，单个 Span 仍受 i32 容量限制；超出直接结果容量明确失败，大内容通过分块复制到调用者提供的 Span 处理，不窄化或回绕长度。

固定位置示例：`A中😀B` 的 length 为 9、characterCount 为 4；`indexOf("😀")` 为 4，`slice(1, 7)` 为 `中😀`，`slice(2, 1)` 报错。

#### 4.3.2 查找、比较与 Unicode 大小写映射

- `indexOf` / `lastIndexOf` 返回 `i64?`，未找到返回 null。`indexOf(needle, start)` 从指定合法字节边界向后查找，省略 start 时从 0 开始；`lastIndexOf` 首版对整个字符串反向查找。空 needle 在任意合法边界匹配，前向返回 start、全串反向返回 length；`contains("")`、`startsWith("")`、`endsWith("")` 为 true。非法 start 仍报范围错误。
- 匹配区分大小写，不自动进行 Unicode 规范化；视觉相同但标量序列不同的字符串不自动相等。字符串次序统一为 Unicode 标量字典序，与合法 UTF-8 的无符号字节字典序一致，前缀相同时短串在前。实现时统一现有 VM 的 UTF-16 CompareOrdinal 与 native 的 UTF-8 字节比较，不保留补充平面字符的两端差异。
- 首版提供 `String.toLower(): String` 与 `String.toUpper(): String`，使用 Unicode 的**无条件完整大小写映射**：取 [SpecialCasing.txt](https://www.unicode.org/Public/17.0.0/ucd/SpecialCasing.txt) 中条件列表为空的对应映射，优先于 UnicodeData.txt 中的简单映射；没有映射的标量保持不变。允许一对多结果，返回 String；不能把映射结果截成单个 char。输出容量不足或长度溢出时报错，不截断映射结果。
- **不支持上下文相关规则，也不支持地区相关规则。** 不应用 `Final_Sigma` 等上下文条件或语言标签条件；每个输入标量的映射与周围字符、操作系统、系统语言和进程 locale 无关。标准库不提供 locale 参数，本地化需求由未来独立的外部国际化库承担。
- 不内置 Case Folding，也不将 toLower/toUpper 当作忽略大小写比较的实现；精确比较继续遵守本节的标量序列规则。不自动归一化输出，不承诺先大写再小写可以恢复原字符串。本契约明确采用无条件映射，不宣称实现包含上下文规则的完整 Default Case Conversion 算法。
- 大小写映射和空白属性统一固定使用 Unicode 17.0.0 数据。数据随库发布并在 VM/native 使用同一版本，不依赖宿主 Unicode 版本或某个名为 Invariant/Root 的宿主函数来决定结果；未来升级 Unicode 数据时作为库兼容性变更同步规范与固定样例。

大小写固定样例：

- `"straße".toUpper()` 得到 `"STRASSE"`，其中 ß 映射为两个标量 SS。
- `"İ".toLower()` 得到 `i` 后跟 U+0307，共两个标量；普通 `"I".toLower()` 得到 `"i"`，不因系统语言为土耳其语而改变。
- `"ΟΣ".toLower()` 得到 `"οσ"`，末尾的 Σ 仍映射为 σ；单独的 `"Σ".toLower()` 同样得到 `"σ"`。

这些样例对应 UCD 的无条件映射及对有条件规则的明确排除，不通过宿主大小写 API 的结果反向定义规范。

#### 4.3.3 分割、替换、裁剪与拼接

- `split(separator)` 按字面分隔串从左到右、不重叠地匹配，立即返回 `Array\<String>`。默认保留开头、中间和末尾空项，空输入得到单个空串；空 separator 报参数错误，不隐式按字符分割。可指定正数 `maxParts: i32`，达到上限后剩余文本作为最后一项，1 表示不分割；默认只受结果容器容量限制。不使用正则，不自动 trim 各项。
- `replace(old, replacement)` 替换全部从左到右的不重叠匹配，`replaceFirst` 只替换首个。replacement 按字面插入，可以为空，新插入文本不参与本次匹配。空 old 报参数错误，无匹配时结果内容等于原串；例如 `"aaa"` 中将 `"aa"` 替换为 `"b"` 得到 `"ba"`。
- `trim` / `trimStart` / `trimEnd` 删除相应两端或单端的 Unicode White_Space 标量，不删除内部空白。按 [Unicode 17.0 White_Space 数据](https://www.unicode.org/Public/17.0.0/ucd/PropList.txt)，包括 NBSP 与全角空格，不包括 U+200B 和 U+FEFF，不把所有不可见字符都当作空白。
- `join` 接收字符串分隔符及 `IEnumerable\<String>`，保留空元素，空序列得到空串；不隐式过滤元素或将 null 当空串。只遍历一次，回调及枚举器异常正常传播。
- 切片、分割、替换、大小写映射及拼接结果均保持 String 的不可变值语义，不暴露可写的共享底层数据。

#### 4.3.4 StringBuilder 与编码

- StringBuilder 提供 String/char 的 append、clear、length、toString；length 是 i64 字节数，其他类型由调用者先调用既有 toString。构造过程应避免反复复制已累计内容。
- toString 生成独立结果但不清空 builder；后续 append 或 clear 不改变已有结果。StringBuilder 使用 local 类型，不支持同实例并发；不因管理内存而要求 IDisposable。首版不加入随机插入、删除或格式模板语言；容量与结果长度溢出明确报错，不截断内容。
- 编码器只处理 String/char 与字节缓冲区之间的转换，不依赖文件或流；流适配器放在 `core.io`。增量解码跨块保留未完成序列，区分“还需要输入”和“最终输入已截断”。
- 默认严格拒绝非法编码，替换模式须显式选择。首版内置 UTF-8，不默认跟随操作系统代码页；文本流的 BOM 和行处理见 §4.4。char 的 32 位存储不改变 String 的 UTF-8 表示。

#### 4.3.5 整数与浮点解析

在 `core.text` 中为 `i8`、`i16`、`i32`、`i64`、`u8`、`u16`、`u32`、`u64` 及 `float`、`double` 声明公开静态扩展方法 `parse` / `tryParse`，通过目标类型调用，例如 `u32.parse(...)`。parse 返回目标类型，tryParse 返回该类型的可空值。32 位和 64 位浮点沿用 Rigi 源码类型名 float 和 double；f32/f64 表示对应精度，不在此新增类型别名。bool 不提供解析 API。

整数方法统一接收 `IntegerRadix` 枚举参数，包含 `.Decimal`、`.Hexadecimal`、`.Binary`、`.Octal`，默认 `.Decimal`。浮点方法不接收整数进制参数。代表性声明如下（省略方法体，其余整数类型采用相同形状并返回各自类型）：

```rigi
pub ext static func u32.parse(text: String, radix: IntegerRadix = .Decimal): u32 { ... }
pub ext static func u32.tryParse(text: String, radix: IntegerRadix = .Decimal): u32? { ... }
pub ext static func float.parse(text: String): float { ... }
pub ext static func float.tryParse(text: String): float? { ... }
pub ext static func double.parse(text: String): double { ... }
pub ext static func double.tryParse(text: String): double? { ... }
```

同一目标类型的 parse 与 tryParse 使用相同的语法及范围规则；不从 String 的隐式转换触发解析，不依赖尚不存在的 Numeric 泛型协议。

**整数解析：**

- **进制完全由 radix 参数决定，不做前缀自动识别。** 默认只按十进制解析；即使文本以 `0x` 开头，也不能自动切换为十六进制。前导零不改变选定进制，不提供 Auto 模式。
- `.Hexadecimal` 模式要求 `0x` 或等价的 `0X` 前缀，随后至少一个十六进制数字，接受 ASCII 0–9/a–f/A–F；前缀只用于校验该模式的文本形态，不负责选择进制。没有前缀的 `FF` 也不能在此模式读取。
- `.Decimal`、`.Binary`、`.Octal` 模式均使用无前缀数字，分别读取 ASCII 0–9、0–1、0–7；不识别或剥离 `0b` / `0B`、`0o` / `0O`。例如二进制模式读 `0b1010` 时先取得数字 0，再在 b 处停止，结果为 0；八进制模式读 `0o17` 同理。
- 可有一个前导 `+` 或 `-`，十六进制时符号必须在 `0x` / `0X` 之前，其他模式在数字之前。无符号目标拒绝任何负号，包括 `-0` 和 `-0x0`。
- **整数解析读取有效数字前缀，不要求消费完整文本。** 处理可选符号及所选模式要求的前缀后，连续读取该进制的有效数字；遇到第一个非有效数字字符或文本结束即停止，返回已读取数字的值。默认十进制读 `0xFF` 时取得 0，在 x 处停止，剩余 xFF 作为后缀不再解析。
- 不跳过前导空白；空串、仅符号、所需十六进制前缀缺失，或处理符号/前缀后没有任何有效数字时失败。数字后面的空白、逗号、下划线、字母或其他非该进制数字的字符只是停止位置，不因这些后缀使已读数字失效，也不赋予它们分组符、类型后缀或其他特殊意义。数字和进制前缀使用 ASCII，不依赖系统语言。
- 各进制均按完整的有效数字前缀所表示的数学数值检查目标宽度及符号范围，不将十六进制文本解释为目标类型的补码位模式；最小负数须正确解析，越界均失败，不回绕或先经过浮点。不能因溢出而提前成功返回更短的数字前缀；例如 `i8.parse("128abc")` 仍然超出范围。

固定示例：

- `i32.parse("010")` 为 10，`i32.parse("010", .Octal)` 为 8。
- `i32.parse("0xFF", .Hexadecimal)` 为 255，`i32.parse("17", .Octal)` 为 15，`i32.parse("1010", .Binary)` 为 10。
- `i8.parse("-0x80", .Hexadecimal)` 为 -128；`i8.parse("0xFF", .Hexadecimal)` 超出范围，不解释为 -1。
- `i32.parse("0xFF")` 和 `i32.parse("0xFF", .Decimal)` 均为 0；`i32.parse("123abc")` 为 123，`i32.parse("12 34")` 为 12，`i32.parse("1_000")` 为 1。
- `i32.parse("0b1010", .Binary)` 和 `i32.parse("0o17", .Octal)` 均为 0；`i32.parse("102", .Binary)` 读取 10 后在 2 处停止，结果为 2；`i32.parse("0xFFG", .Hexadecimal)` 为 255。
- `i32.parse("8", .Octal)` 因没有有效数字失败；十六进制模式的 `FF`、`0x`、`0x-1`，以及空串、仅符号或以空白开头的文本也失败。tryParse 对这些失败返回 null，对上述成功的数字前缀返回对应值，包括 0。

**浮点解析：**

- 接受十进制整数形式、小数形式和 e/E 指数形式，可有前导正负号及前导零。整数部分至少一位数字；出现小数点后也至少一位数字；指数可有正负号且至少一位数字。`1`、`+01.25`、`1e-3` 合法，`.5`、`1.`、`1e` 非法。
- 只接受 ASCII 数字和点号小数点，不接受空白、地区格式、进制前缀、十六进制浮点或 f/F 后缀，且必须消费全部文本。整数入口的多进制支持不扩展浮点语法。
- 直接按目标 float/double 精度作最近值、正中取偶的舍入；不先解析为 double 再声称 float 一定正确舍入。支持次正规数，过小的有限数按同一规则舍入至带符号零并视为成功，保留负零。
- 有限文本超出目标有限范围时报错，不自动产生 Infinity；普通十进制不能精确二进制表示不单独构成失败。
- 接受区分大小写的 `NaN`、`Infinity`、`+Infinity`、`-Infinity`，与既有 toString 输出衔接；不接受其他大小写或缩写，不承诺保留 NaN 的载荷或符号。

**失败与格式边界：**

- parse 失败抛一个 `core.text.NumberParseException`，区分非法文本和超出范围，并可给出 String 内零基 UTF-8 字节位置。tryParse 对这两类失败返回 null，0 是正常值；其他异常照常传播，不通过 catch 所有异常伪装成 null。
- 空分隔串等文本方法参数错误使用普通 `core.text.TextArgumentException`；切片范围和边界错误按 §4.3.1 使用 `core.OutOfBoundException`。沿用现有单异常传播模型。
- 数值输出复用既有 toString、最短往返浮点及固定地区无关格式。Rigi 实现解析流程，底层只补必要且两端一致的字符串/数值原语；VM/native 不直接继承宿主默认的宽松空白、溢出或地区行为。
- JSON 继续执行 D4 的独立语法、完整消费及值域检查，不能仅以整数 parse 成功判断整个 JSON 数字合法。JSON 中的 `0xFF`、`123abc` 仍报错，目标为整数时 `3.0`、`3e0` 也不能通过读取前缀 3 蒙混过关。通用整数前缀解析不放宽 JSON，也不改变本节浮点入口的完整文本校验。

