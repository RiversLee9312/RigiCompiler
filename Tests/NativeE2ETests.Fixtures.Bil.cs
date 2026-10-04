using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;
using RigiCompiler.Middleware.Cli;
using RigiCompiler.Middleware.Emit;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Tests
{
    public static partial class NativeE2ETests
    {
        // Fixtures.Bil 职责；与主文件共享同一类型、字段及生命周期。

        // to_string 面族：手写 BIL 声明 native fn（不走 stdlib），VM hook 与
        // native 产物 stdout 对拍。f64/f32 走 Ryu 最短往返 + .NET 默认呈现。
        private const string ToStringFacesBil =
            "BIL \"1.1\"\n" +
            "\n" +
            "Metadata {\n" +
            "    module = string \"strfmt\"\n" +
            "}\n" +
            "\n" +
            "Resources {\n" +
            "    R_I0 = i64 0,\n" +
            "    R_Ineg = i64 -42,\n" +
            "    R_F0 = f64 0,\n" +
            "    R_Fneg = f64 -2.5,\n" +
            "    R_F01 = f64 0.1,\n" +
            "    R_F02 = f64 0.2,\n" +
            "    R_F05 = f64 0.5,\n" +
            "    R_F1 = f64 1,\n" +
            "    R_F3 = f64 3,\n" +
            "    R_F4 = f64 4,\n" +
            "    R_F4n = f64 -4,\n" +
            "    R_F10 = f64 10,\n" +
            "    R_F1em5 = f64 1e-5,\n" +
            "    R_F1e15 = f64 1e15,\n" +
            "    R_F1e16 = f64 1e16,\n" +
            "    R_F1e17 = f64 1e17,\n" +
            "    R_F1e20 = f64 1e20,\n" +
            "    R_F1e308 = f64 1e308,\n" +
            "    R_Fmax = f64 1.7976931348623157e308,\n" +
            "    R_Fmin = f64 2.2250738585072014E-308,\n" +
            "    R_Fs1 = f32 1,\n" +
            "    R_Fs3 = f32 3,\n" +
            "    R_True = bool true,\n" +
            "    R_False = bool false,\n" +
            "    R_A = char 'A',\n" +
            "    R_Nl = string \"\\n\",\n" +
            "    R_Zero = i32 0\n" +
            "}\n" +
            "\n" +
            "LocalSymbols {\n" +
            "    .method $print(text:.string)@.void priv native symbol(\"print\") lib(\"rigi_rt\")\n" +
            "    .method $i64_to_string(value:.i64)@.string priv native symbol(\"i64_to_string\") lib(\"rigi_rt\")\n" +
            "    .method $f32_to_string(value:.f32)@.string priv native symbol(\"f32_to_string\") lib(\"rigi_rt\")\n" +
            "    .method $f64_to_string(value:.f64)@.string priv native symbol(\"f64_to_string\") lib(\"rigi_rt\")\n" +
            "    .method $bool_to_string(value:.bool)@.string priv native symbol(\"bool_to_string\") lib(\"rigi_rt\")\n" +
            "    .method $char_to_string(value:.char)@.string priv native symbol(\"char_to_string\") lib(\"rigi_rt\")\n" +
            "    .method $main()@.i32 pub entrypoint\n" +
            "}\n" +
            "\n" +
            "ExternalSymbols {\n" +
            "}\n" +
            "\n" +
            "fn($main()@.i32) {\n" +
            "    .args {\n" +
            "        .return = .i32\n" +
            "    }\n" +
            "    .vars {\n" +
            "        .i64 i,\n" +
            "        .f64 f,\n" +
            "        .f64 x,\n" +
            "        .f64 y,\n" +
            "        .f32 fs,\n" +
            "        .f32 xs,\n" +
            "        .f32 ys,\n" +
            "        .bool b,\n" +
            "        .char c,\n" +
            "        .string s,\n" +
            "        .string nl,\n" +
            "        .i32 r\n" +
            "    }\n" +
            "    .block entry entrypoint {\n" +
            "        load res(R_Nl) $nl\n" +
            "        load res(R_I0) $i\n" +
            "        invoke fn($i64_to_string(value:.i64)@.string) $s [$i]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_Ineg) $i\n" +
            "        invoke fn($i64_to_string(value:.i64)@.string) $s [$i]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F0) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_Fneg) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F01) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F05) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F4) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F4n) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F1) $x\n" +
            "        load res(R_F3) $y\n" +
            "        div $x $y $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F01) $x\n" +
            "        load res(R_F02) $y\n" +
            "        add $x $y $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F1e20) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F1em5) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F1e15) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F1e16) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F1e17) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F0) $f\n" +
            "        opposite $f $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_F1e308) $x\n" +
            "        load res(R_F10) $y\n" +
            "        mul $x $y $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_Fmax) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_Fmin) $f\n" +
            "        invoke fn($f64_to_string(value:.f64)@.string) $s [$f]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_Fs1) $xs\n" +
            "        load res(R_Fs3) $ys\n" +
            "        div $xs $ys $fs\n" +
            "        invoke fn($f32_to_string(value:.f32)@.string) $s [$fs]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_True) $b\n" +
            "        invoke fn($bool_to_string(value:.bool)@.string) $s [$b]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_False) $b\n" +
            "        invoke fn($bool_to_string(value:.bool)@.string) $s [$b]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_A) $c\n" +
            "        invoke fn($char_to_string(value:.char)@.string) $s [$c]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$s]\n" +
            "        invoke.noret fn($print(text:.string)@.void) [$nl]\n" +
            "        load res(R_Zero) $r\n" +
            "        ret $r\n" +
            "    }\n" +
            "}\n";

        // string 排序比较的手写 BIL（前端 P3 未放行 String 的 < 运算符，
        // MW3：f64 switch 比较链降级的符号零/NaN 口径（BIL 级，VM
        // ValuesEqual 即 C# ==）：-0.0 selector 命中 0 case、+0.0 selector
        // 命中 -0.0 case（符号零相等）；NaN case 标签永不命中（跳过头项
        // 落第二项）。期望退出码 10+20+40=70
        private const string SwitchF64SignZeroBil =
            "BIL \"1.1\"\n" +
            "\n" +
            "Metadata {\n" +
            "    module = string \"swfsign\"\n" +
            "}\n" +
            "\n" +
            "Resources {\n" +
            "    R_T1 = switch-table<.f64> { 0 },\n" +
            "    R_T2 = switch-table<.f64> { -0.0 },\n" +
            "    R_T3 = switch-table<.f64> { NaN, 2.0 },\n" +
            "    R_NegZero = f64 -0.0,\n" +
            "    R_PosZero = f64 0,\n" +
            "    R_Two = f64 2.0,\n" +
            "    R_0 = i32 0,\n" +
            "    R_10 = i32 10,\n" +
            "    R_20 = i32 20,\n" +
            "    R_40 = i32 40,\n" +
            "    R_1 = i32 1,\n" +
            "    R_2 = i32 2,\n" +
            "    R_4 = i32 4\n" +
            "}\n" +
            "\n" +
            "LocalSymbols {\n" +
            "    .method $main()@.i32 pub entrypoint\n" +
            "}\n" +
            "\n" +
            "ExternalSymbols {\n" +
            "}\n" +
            "\n" +
            "fn($main()@.i32) {\n" +
            "    .args {\n" +
            "        .return = .i32\n" +
            "    }\n" +
            "    .vars {\n" +
            "        .f64 x,\n" +
            "        .i32 acc,\n" +
            "        .i32 .t0,\n" +
            "        .breakid .b0,\n" +
            "        .breakid .b1,\n" +
            "        .breakid .b2\n" +
            "    }\n" +
            "    .block entry entrypoint {\n" +
            "        load res(R_0) $acc\n" +
            "        load res(R_NegZero) $x\n" +
            "        switch $x res(R_T1)\n" +
            "            [blk(i0)]\n" +
            "            blk(d0)\n" +
            "            $.b0\n" +
            "        load res(R_PosZero) $x\n" +
            "        switch $x res(R_T2)\n" +
            "            [blk(i1)]\n" +
            "            blk(d1)\n" +
            "            $.b1\n" +
            "        load res(R_Two) $x\n" +
            "        switch $x res(R_T3)\n" +
            "            [blk(i2a), blk(i2b)]\n" +
            "            blk(d2)\n" +
            "            $.b2\n" +
            "        ret $acc\n" +
            "    }\n" +
            "    .block i0 {\n" +
            "        load res(R_10) $.t0\n" +
            "        add $acc $.t0 $acc\n" +
            "    }\n" +
            "    .block d0 {\n" +
            "        load res(R_1) $.t0\n" +
            "        add $acc $.t0 $acc\n" +
            "    }\n" +
            "    .block i1 {\n" +
            "        load res(R_20) $.t0\n" +
            "        add $acc $.t0 $acc\n" +
            "    }\n" +
            "    .block d1 {\n" +
            "        load res(R_2) $.t0\n" +
            "        add $acc $.t0 $acc\n" +
            "    }\n" +
            "    .block i2a {\n" +
            "        load res(R_4) $.t0\n" +
            "        add $acc $.t0 $acc\n" +
            "    }\n" +
            "    .block i2b {\n" +
            "        load res(R_40) $.t0\n" +
            "        add $acc $.t0 $acc\n" +
            "    }\n" +
            "    .block d2 {\n" +
            "        load res(R_4) $.t0\n" +
            "        add $acc $.t0 $acc\n" +
            "    }\n" +
            "}\n";

        // §11.5 内建形态合法）：cmp.lt/gt/le 三形态 + native print 面输出。
        // 结构模仿前端产物（if 块 + breakid），VM 侧经 rigi_rt/print hook 执行
        private const string StringOrderBil =
            "BIL \"1.1\"\n" +
            "\n" +
            "Metadata {\n" +
            "    module = string \"strorder\"\n" +
            "}\n" +
            "\n" +
            "Resources {\n" +
            "    R_A = string \"abc\",\n" +
            "    R_B = string \"abd\",\n" +
            "    R_Lt = string \"str lt ok\\n\",\n" +
            "    R_Gt = string \"str gt ok\\n\",\n" +
            "    R_Le = string \"str le ok\\n\",\n" +
            "    R_Zero = i32 0\n" +
            "}\n" +
            "\n" +
            "LocalSymbols {\n" +
            "    .method $main()@.i32 pub entrypoint\n" +
            "}\n" +
            "\n" +
            "ExternalSymbols {\n" +
            "    .type core.io::Console = class pub {\n" +
            "        .static-method core.io::Console$.static.print(value:.string)@.void priv native symbol(\"print\") lib(\"rigi_rt\")\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "fn($main()@.i32) {\n" +
            "    .args {\n" +
            "        .return = .i32\n" +
            "    }\n" +
            "    .vars {\n" +
            "        .string a,\n" +
            "        .string b,\n" +
            "        .breakid .b0,\n" +
            "        .breakid .b1,\n" +
            "        .breakid .b2,\n" +
            "        .bool .t0,\n" +
            "        .bool .t1,\n" +
            "        .bool .t2,\n" +
            "        .string .t3,\n" +
            "        .string .t4,\n" +
            "        .string .t5,\n" +
            "        .i32 .t6\n" +
            "    }\n" +
            "    .block entry entrypoint {\n" +
            "        load res(R_A) $a\n" +
            "        load res(R_B) $b\n" +
            "        cmp.lt $a $b $.t0\n" +
            "        if $.t0 blk(if0-then) none $.b0\n" +
            "        cmp.gt $b $a $.t1\n" +
            "        if $.t1 blk(if1-then) none $.b1\n" +
            "        cmp.le $a $a $.t2\n" +
            "        if $.t2 blk(if2-then) none $.b2\n" +
            "        load res(R_Zero) $.t6\n" +
            "        ret $.t6\n" +
            "    }\n" +
            "    .block if0-then {\n" +
            "        load res(R_Lt) $.t3\n" +
            "        invoke.noret fn(core.io::Console$.static.print(value:.string)@.void) [$.t3]\n" +
            "    }\n" +
            "    .block if1-then {\n" +
            "        load res(R_Gt) $.t4\n" +
            "        invoke.noret fn(core.io::Console$.static.print(value:.string)@.void) [$.t4]\n" +
            "    }\n" +
            "    .block if2-then {\n" +
            "        load res(R_Le) $.t5\n" +
            "        invoke.noret fn(core.io::Console$.static.print(value:.string)@.void) [$.t5]\n" +
            "    }\n" +
            "}\n";

        // nullable 双空互比的手写 BIL（前端 P3 未放行两 nullable 变量互比，
        // §11.5 内建形态合法）：两个 .nullable<.string> 同载 null 资源后
        // cmp.eq 为真、cmp.ne 为假（胖引用双段零恒等）
        private const string NullBothBil =
            "BIL \"1.1\"\n" +
            "\n" +
            "Metadata {\n" +
            "    module = string \"nullboth\"\n" +
            "}\n" +
            "\n" +
            "Resources {\n" +
            "    R_Null = null type(.string),\n" +
            "    R_Eq = string \"both null eq\\n\",\n" +
            "    R_Bad = string \"BAD\\n\",\n" +
            "    R_Zero = i32 0\n" +
            "}\n" +
            "\n" +
            "LocalSymbols {\n" +
            "    .method $main()@.i32 pub entrypoint\n" +
            "}\n" +
            "\n" +
            "ExternalSymbols {\n" +
            "    .type core.io::Console = class pub {\n" +
            "        .static-method core.io::Console$.static.print(value:.string)@.void priv native symbol(\"print\") lib(\"rigi_rt\")\n" +
            "    }\n" +
            "}\n" +
            "\n" +
            "fn($main()@.i32) {\n" +
            "    .args {\n" +
            "        .return = .i32\n" +
            "    }\n" +
            "    .vars {\n" +
            "        .nullable<.string> a,\n" +
            "        .nullable<.string> b,\n" +
            "        .breakid .b0,\n" +
            "        .breakid .b1,\n" +
            "        .bool .t0,\n" +
            "        .bool .t1,\n" +
            "        .string .t2,\n" +
            "        .string .t3,\n" +
            "        .i32 .t4\n" +
            "    }\n" +
            "    .block entry entrypoint {\n" +
            "        load res(R_Null) $a\n" +
            "        load res(R_Null) $b\n" +
            "        cmp.eq $a $b $.t0\n" +
            "        if $.t0 blk(if0-then) none $.b0\n" +
            "        cmp.ne $a $b $.t1\n" +
            "        if $.t1 blk(if1-then) none $.b1\n" +
            "        load res(R_Zero) $.t4\n" +
            "        ret $.t4\n" +
            "    }\n" +
            "    .block if0-then {\n" +
            "        load res(R_Eq) $.t2\n" +
            "        invoke.noret fn(core.io::Console$.static.print(value:.string)@.void) [$.t2]\n" +
            "    }\n" +
            "    .block if1-then {\n" +
            "        load res(R_Bad) $.t3\n" +
            "        invoke.noret fn(core.io::Console$.static.print(value:.string)@.void) [$.t3]\n" +
            "    }\n" +
            "}\n";

    }
}
