// Rigi 标准库：异常根与具体子类（SYNTAX §8.1）。
// 异常根本文件源码声明（abstract class，Object 基由 class 默认基类填充；
// protected message 字段 + pub abstract getMessage）。
// 子类自持显式 init（Rigi 无 super 构造语法——init 体直接赋值继承字段
// message，RangeEnumerator 先例；参数名 text 避开字段名防遮蔽）。
// getMessage 按用户裁定为多态抽象：各子类 override 返回自身 message 字段
// （中间层 RuntimeException.getMessage 标 open 供再派生覆写；叶类
// IOException/CastException/NoSuchMethodException/DividedByZeroException
// 直接覆写）。
// 层级：具体子类一律继承 RuntimeException（§8 示例 catch IOException
// 与 catch RuntimeException 并列，说明二者兼容）。
// MW9b：内置异常消息模板源码化——各子类在保留 init(text: String)
// （用户代码直接抛用）之外新增便捷 init 重载，把消息模板烘进本源
// 文件（动态部分作实参，字符串插值拼接，§3.3/§3.8）；VM 与
// Middleware 两宿主构造内置异常时经真 init 派发，消息天然一致。
// 模板关键字子串（整数除以零 / 无法将 … 转换为 / 不匹配任何 init /
// 数组下标越界）为既有测试断言锚点，改动模板必须保留。
namespace core

pub abstract class Exception {
    protected var message: String
    pub abstract func getMessage(): String
}

pub open class RuntimeException : core.Exception {
    pub init(text: String) { message = text }
    pub open override func getMessage(): String { return message }
}

// 只读 Cell 或 Object 身份不提供可替换值槽的能力。
pub class ImmutablePlaceException : RuntimeException {
    pub init() { message = "目标 Place 不可写" }
    pub init(text: String) { message = text }
    pub override func getMessage(): String { return message }
}

pub open class IOException : RuntimeException {
    pub init(text: String) { message = text }
    pub override func getMessage(): String { return message }
}

pub open class CastException : RuntimeException {
    pub init(text: String) { message = text }
    // 类型转换失败（as/nullable 展开）：源/目标类型全名作实参
    pub init(fromType: String, toType: String) {
        message = "无法将 ${fromType} 转换为 ${toType}"
    }
    pub override func getMessage(): String { return message }
}

pub open class NoSuchMethodException : RuntimeException {
    pub init() { message = "未路由的降级请求" }
    pub init(text: String) { message = text }
    // new.indirect 动态构造/run 期 init 重载解析失败：目标类型全名作实参
    pub init(typeName: String) {
        message = "new.indirect 目标不可构造：不匹配任何 init：${typeName}"
    }
    pub override func getMessage(): String { return message }
}

pub open class DividedByZeroException : RuntimeException {
    pub init(text: String) { message = text }
    // 整数除零（float/double 不抛，IEEE 754 产 inf/NaN）
    pub init() { message = "整数除以零" }
    pub override func getMessage(): String { return message }
}

// 数组/Span 写越界（MW9b 起可捕获；读越界仍按空安全得 null，不抛）
pub open class OutOfBoundException : RuntimeException {
    pub init(text: String) { message = text }
    pub init(index: i64, length: i64) {
        message = "数组下标越界：${index}（长度 ${length}）"
    }
    pub override func getMessage(): String { return message }
}

// 对象当前状态不允许该操作（MW11c）：重复启动已启动 Task（§4.5）、
// Timer.RepeatOption 非正 repeatCount（RUNTIME §19.5）等
pub open class IllegalStateException : RuntimeException {
    pub init(text: String) { message = text }
    pub override func getMessage(): String { return message }
}

// 集合/映射缺键或枚举器无当前元素（MW11d-B1）：Parcel.getElement
// 对 absent key 抛出；与「存了 null」区分（后者返回 null）
pub open class NoSuchElementException : RuntimeException {
    pub init(text: String) { message = text }
    pub override func getMessage(): String { return message }
}
