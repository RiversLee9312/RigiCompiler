// WRAP-001：收尾检查不得放过缺初值，错误初值不得被合成默认 init 豁免。
// expect-error: has no constructor that assigns non-nullable field 'n'
// expect-error: initializer must be of type 'i64'
class Early { pub var payload: Missing = new Missing() }
rich struct EarlyValue { pub var payload: Invalid = new Invalid() }
class Missing { pub var n: i64 }
class Invalid { pub var n: i64 = "bad" }
pub func main(): i32 { return 0 }
