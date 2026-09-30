# Resources 文本格式 / 完整文本示例（§19–§20）

> 本文件是 [BIL_STANDARD.md](../BIL_STANDARD.md)（BIL 标准）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 19. Resources 文本格式

### 19.1 标量资源

规范形式：

```bil
Resources {
    R_Message = string "hello, world",
    R_Enabled = bool true,
    R_Count = i64 123,
    R_Ratio = f64 0.5,
    R_NullUser = null type(com.example::User)
}
```

整数与浮点资源必须显式写类型，避免解析器依赖源码默认字面量规则。

char 资源单引号包围，解码后必须恰好一个 Unicode 标量（U+0000–U+10FFFF，排除 U+D800–U+DFFF；`.char` 32 位标量，`STDLIB/04-text.md` §4.3.1）。转义与 string 资源同一套；补充平面标量的规范承载形式是 `\u{hex}`（1–6 位十六进制）：解码时 ≤0xFFFF 展开为单码元、>0xFFFF 展开为代理对，随后合成单个标量——BIL 文本直写的代理对字符同样在此合成；孤立代理与多标量内容都拒绝。

`null type(T)` 标注**元素类型** `T`，资源本身的类型为对应的 `.nullable<T>`——因此它可以直接与 `.nullable<T>` 变量做 `cmp.eq` / `cmp.ne` 比较而满足 §11.5 的类型严格相同规则，这就是 §3.4 所称「nullable 检查」的标准形态。

判别值资源（§8.5）：enum case 的 `discriminant res(R)` 使用非负整数标量资源（如 `R_Disc_0 = i32 0`）；其静态类型必须是非负整数标量，判别值与宽度检查由 frontend/verifier 按 `RUNTIME.md` §16.4/§16.1 执行。

### 19.2 数组、Pair 与 Map

```bil
R_Names = array<string> { "a", "b" }
R_Entry = pair<string, i64> { "count", 2 }
R_Map = map<string, i64> {
    "a" = 1,
    "b" = 2
}
```

复合资源的元素类型必须严格一致。

### 19.3 原始数据

```bil
R_DataHex = raw.hex x2FF2331C
R_DataBin = raw.bin b01010101
```

原始数据只表示不可变 byte sequence，不自动视为 typeid、fieldid 或 Native 地址。

### 19.4 switch table

```bil
R_Switch = switch-table<.i32> { 1, 2, 3 }
```

元素必须为编译期常量，且类型与 selector 严格相同。

### 19.5 catch table

```bil
R_Catches = catch-table {
    type(core::IOException) -> blk(catchIo),
    type(core::RuntimeException) -> blk(catchRuntime)
}
```

catch 顺序具有语义，不能重排。

---

## 20. 完整文本示例

```bil
BIL "1.1"

Metadata {
    module = string "com.example.app"
}

Resources {
    R_Hello = string "hello, world",
    R_Zero = i32 0
}

LocalSymbols {
    .type com.example::App = class pub {
        .static-method com.example::App$.static.main(args:.array<.string>)@.i32 pub entrypoint
    }
}

ExternalSymbols {
    .type core::Console = class pub {
        .static-method core::Console$.static.println(value:.string)@.void pub
    }
}

fn(com.example::App$.static.main(args:.array<.string>)@.i32) {
    .args {
        .return = .i32,
        args = .array<.string>
    }

    .vars {
        .string message,
        .i32 result
    }

    .block entry entrypoint {
        load res(R_Hello) $message
        invoke.noret fn(core::Console$.static.println(value:.string)@.void) [$message]
        load res(R_Zero) $result
        ret $result
    }
}
```

wrapper 应用标记与 proxy 模板示例：

```bil
LocalSymbols {
    .type com.example::Service = class pub wrapped(core.logging::Logged) {
        .field com.example::Service#name@.string pub
    }

    .type core.logging::Logged = wrapper generic(TTarget) rich {
        .method core.logging::Logged$.proxy.get.name(value:.string)@.string
            pub wrapper-proxy(specific)
    }
}
```

---
