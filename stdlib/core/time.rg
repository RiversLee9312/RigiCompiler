// Rigi 标准库：core.time 时间类型面（MW11c，RUNTIME §19.7）——支撑
// core.coroutine.Timer 的最小时间面。DateTime.now() 的 native 时钟原语
//（§17.4 rigi_time_now，@NativeSymbol("time_now")）经 rigi_rt 落地。
//   - TimeStamp：时刻戳——milliseconds（1970/1/1 00:00 UTC 起毫秒，
//     负数为该时刻前）+ nanoseconds（毫秒外多出的纳秒，访问器 setter
//     限 0..999_999，越界抛 core.OutOfBoundException）；总纳秒 =
//     milliseconds * 1_000_000 + nanoseconds。
//   - DateTime：包一个 TimeStamp；now() 经 native 时钟原语；减法得
//     TimeSpan、比较经 compareTo/equals。
//   - TimeSpan：包 i64 毫秒（priv 字段 + 只读 totalMilliseconds）；
//     fromMilliseconds 构造入口；比较经 compareTo/equals。
namespace core.time

pub struct TimeStamp {
    pub const milliseconds: i64

    // setter 限范围 0..999_999（越界抛 core.OutOfBoundException）；
    // backing 形态：进入时隐含 backing = value，体只做校验
    pub var nanoseconds: i32 {
        pub get
        pub set(value: _) {
            if ((value < 0) or (value > 999999)) {
                throw new core.OutOfBoundException(
                    "TimeStamp.nanoseconds 越界：${value}（范围 0..999999）")
            }
        }
    }

    pub init(_ -> milliseconds, _ -> nanoseconds) { }
}

pub struct TimeSpan {
    priv const milliseconds: i64

    priv init(_ -> milliseconds) { }

    // 构造入口（§19.7）
    pub static func fromMilliseconds(milliseconds: i64): TimeSpan {
        return new TimeSpan(milliseconds)
    }

    // 只读属性：包内 i64 毫秒
    pub var totalMilliseconds: i64 {
        pub get(_: _) { return milliseconds }
    }

    pub operator compareTo(other: TimeSpan): core.ComparisonResult {
        if (milliseconds < other.milliseconds) { return .LesserThanAnother }
        if (milliseconds > other.milliseconds) { return .GreaterThanAnother }
        return .Equal
    }

    pub operator equals(other: TimeSpan): bool {
        return milliseconds == other.milliseconds
    }
}

pub struct DateTime {
    pub const stamp: TimeStamp

    pub init(_ -> stamp) { }

    // native 时钟原语底座（§17.4 rigi_time_now：UTC 起毫秒；棒3 落地）
    pub static func now(): DateTime {
        return new DateTime(new TimeStamp(rigi_time_now(), 0))
    }

    // 两个 DateTime 相减得 TimeSpan（毫秒差；纳秒差不进 TimeSpan 形状）
    pub operator minus(other: DateTime): TimeSpan {
        return TimeSpan.fromMilliseconds(stamp.milliseconds - other.stamp.milliseconds)
    }

    pub operator compareTo(other: DateTime): core.ComparisonResult {
        if (stamp.milliseconds < other.stamp.milliseconds) { return .LesserThanAnother }
        if (stamp.milliseconds > other.stamp.milliseconds) { return .GreaterThanAnother }
        if (stamp.nanoseconds < other.stamp.nanoseconds) { return .LesserThanAnother }
        if (stamp.nanoseconds > other.stamp.nanoseconds) { return .GreaterThanAnother }
        return .Equal
    }

    pub operator equals(other: DateTime): bool {
        return (stamp.milliseconds == other.stamp.milliseconds) and (stamp.nanoseconds == other.stamp.nanoseconds)
    }
}

// 时钟原语（§17.4；与 core.coroutine 的 rigi_time_now 同一 native 符号
// time_now，本文件私有声明供 DateTime.now 触达；@NativeSymbol 必带——
// 缺省符号经 rigi_rt 前缀拼接会落空成 rigi_rigi_time_now）
@NativeLibrary("rigi_rt")
@NativeSymbol("time_now")
priv native func rigi_time_now(): i64
