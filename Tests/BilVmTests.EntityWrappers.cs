using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // EntityWrappers 职责；与主文件共享同一类型、字段及生命周期。

        // Entity 派发 a)：specific 方法 proxy 环绕 inner——前后各 println，
        // 验证顺序与返回值（proxy 可改返回值）。
        private static void TestEntitySpecificMethodProxySurrounds()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Around {\n" +
                "    pub init()\n" +
                "    operator .proxy.fetch(x: i32): i32 {\n" +
                "        core.io.Console.println(\"before\")\n" +
                "        var r = inner(x)\n" +
                "        core.io.Console.println(\"after\")\n" +
                "        return (r + 1)\n" +
                "    }\n" +
                "}\n" +
                "@Around\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func fetch(x: i32): i32 {\n" +
                "        core.io.Console.println(\"body\")\n" +
                "        return (x * 2)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.fetch(21)\n" +
                "}\n");
            CheckOk("Entity specific 方法 proxy 环绕", result);
            TestHarness.Check("环绕 stdout 顺序", result.Stdout,
                "before\n" +
                "body\n" +
                "after\n");
            CheckI32("proxy 改返回值 21*2+1", result, 43);
        }

        // Entity 派发 b)：wildcard 方法 proxy 两方向——已声明成员无 specific
        // 时落 wildcard 并经 inner 到原始方法；未声明成员经 call??? 进入同一
        // wildcard 并被 proxy 直接路由成功（不 inner）。
        private static void TestEntityWildcardMethodProxyBothDirections()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Router {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        core.io.Console.println(symbol)\n" +
                "        if (symbol == \"Service$fetchUserById(.i32)@.any\") {\n" +
                "            return (99 as TReturn)\n" +
                "        } else {\n" +
                "            return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "        }\n" +
                "    }\n" +
                "}\n" +
                "@Router\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.ping(41)\n" +
                "    var b = (s.fetchUserById(42) as i32)\n" +
                "    return ((a * 1000) + b)\n" +
                "}\n");
            CheckOk("Entity wildcard 两方向", result);
            CheckI32("ping 经 inner=42、fetchUserById 被 proxy 路由=99", result, 42099);
            TestHarness.CheckTrue("wildcard 收到已声明 symbol",
                result.Stdout.Contains("Service$ping") == true, result.Stdout);
            TestHarness.CheckTrue("wildcard 收到未声明 symbol",
                result.Stdout.Contains("Service$fetchUserById") == true, result.Stdout);
        }

        // Entity 派发 c)：getter/setter proxy——宿主字段读写各绕一层并计数。
        private static void TestEntityGetterSetterProxyCounts()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Counting {\n" +
                "    pub var gets: i32\n" +
                "    pub var sets: i32\n" +
                "    pub init() {\n" +
                "        gets = 0\n" +
                "        sets = 0\n" +
                "    }\n" +
                "    operator .proxy.get.name\\<TField>(value: TField): TField {\n" +
                "        gets = (gets + 1)\n" +
                "        return value\n" +
                "    }\n" +
                "    operator .proxy.set.name\\<TField>(value: TField) {\n" +
                "        sets = (sets + 1)\n" +
                "        inner(value)\n" +
                "    }\n" +
                "}\n" +
                "@Counting\n" +
                "pub class Service {\n" +
                "    pub var name: String\n" +
                "    pub init() { name = \"a\" }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    s.name = \"b\"\n" +
                "    var n = s.name\n" +
                "    var ok1 = ((n == \"b\") and (s:Counting.gets == 1))\n" +
                "    var ok2 = (ok1 and (s:Counting.sets == 1))\n" +
                "    if (ok2) { return 1 } else { return 0 }\n" +
                "}\n");
            CheckOk("Entity getter/setter proxy 计数", result);
            CheckI32("写读各绕一层且字段生效", result, 1);
        }

        // Entity 派发 e)：operator proxy——frontend 的 `+` 不查用户 operator
        //（TestUserOperatorAdd 同因），直接发 add 指令验证 `+` 命中
        // .proxy.opr.*（wildcard proxy 直接返回 (99,0) 的 Vec）。
        private static void TestEntityOperatorProxyWildcardDirectModule()
        {
            var result = BilVm.Run(WrappedVecOperatorProxyModule());
            CheckOk("Entity operator .proxy.opr.* 直构", result);
            CheckI32("+ 命中 .proxy.opr.* 返回 99", result, 99);
        }

        // Entity 派发 f)：proxy 状态跨调用持久（计数器累加，值参与返回值）。
        private static void TestEntityProxyStatePersists()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Counting {\n" +
                "    pub var calls: i32\n" +
                "    pub init() { calls = 0 }\n" +
                "    operator .proxy.fetch(x: i32): i32 {\n" +
                "        calls = (calls + 1)\n" +
                "        return (inner(x) + calls)\n" +
                "    }\n" +
                "}\n" +
                "@Counting\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func fetch(x: i32): i32 { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    var a = s.fetch(10)\n" +
                "    var b = s.fetch(10)\n" +
                "    if (((a == 11) and (b == 12))) { return 1 } else { return 0 }\n" +
                "}\n");
            CheckOk("Entity proxy 状态持久", result);
            CheckI32("第 1 次 11、第 2 次 12", result, 1);
        }

        // Entity 派发 g)：get.self——Entity proxy 体内读宿主字段。
        private static void TestEntityProxySelfReadsHostField()
        {
            var result = Run(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W\\<TTarget> {\n" +
                "    pub init()\n" +
                "    operator .proxy.peek(): i32 {\n" +
                "        var host = (self as Service)\n" +
                "        return host.n\n" +
                "    }\n" +
                "}\n" +
                "@W\n" +
                "pub class Service {\n" +
                "    pub var n: i32\n" +
                "    pub init() { n = 42 }\n" +
                "    pub func peek(): i32 { return 0 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return s.peek()\n" +
                "}\n");
            CheckOk("Entity proxy self 读宿主字段", result);
            CheckI32("self.n == 42", result, 42);
        }

        // Entity 派发 h)：负例直构——未绑定语境下 .generic 参与 cast 抛
        // VmException（执行期类型操作绝不恒等放行）。
        private static void TestEntityGenericCastUnboundDirectModule()
        {
            var result = BilVm.Run(UnboundGenericCastModule());
            TestHarness.CheckTrue("未绑定 .generic cast 抛 VmException",
                result.Exception != null
                && result.Exception.Message.Contains("无法解析泛型占位")
                && result.Exception.Message.Contains(".generic<$.generic.T>"),
                result.Exception?.ToString() ?? "<null>");
        }

    }
}
