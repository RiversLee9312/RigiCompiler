// wrapper 借用不能通过普通值或闭包脱离宿主。
// expect-error: Wrapper 'this' cannot be used as a value
@WrapperTarget(.Entity)
wrapper Borrowed {
    pub var value: i32
    pub init() { value = 7 }
    pub func read(): i32 { return this.value }
    pub func leak() { consume(this) }
}
func consume\<T>(value: T) {}
@Borrowed
class Owner { pub init() }
pub func main(): i32 { return 0 }
