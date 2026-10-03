// WRAP-001：省略 init 的 wrapper 安装仍检查非空字段定值。
// expect-error: has no constructor that assigns non-nullable field 'reads'
class Host { @W
 pub func read(): i64 { return 0L } }
@WrapperTarget(.Method)
wrapper W { pub var reads: i64 }
pub func main(): i32 { return 0 }
