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
namespace core

pub abstract class Exception {
    protected var message: String
    pub abstract func getMessage(): String
}

pub open class RuntimeException : core.Exception {
    pub init(text: String) { message = text }
    pub open override func getMessage(): String { return message }
}

pub open class IOException : RuntimeException {
    pub init(text: String) { message = text }
    pub override func getMessage(): String { return message }
}

pub open class CastException : RuntimeException {
    pub init(text: String) { message = text }
    pub override func getMessage(): String { return message }
}

pub open class NoSuchMethodException : RuntimeException {
    pub init(text: String) { message = text }
    pub override func getMessage(): String { return message }
}

pub open class DividedByZeroException : RuntimeException {
    pub init(text: String) { message = text }
    pub override func getMessage(): String { return message }
}
