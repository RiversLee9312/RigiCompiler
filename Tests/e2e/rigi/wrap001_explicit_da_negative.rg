// WRAP-001：声明初值只满足自身字段，其他非空字段仍必须赋值。
// expect-error: is not definitely assigned
@WrapperTarget(.Entity)
wrapper W {
 pub var reads: i64 = 7L
 pub var missing: i64
 pub init() { }
}
@W
class Host { }
pub func main(): i32 { return 0 }
