// P3 局部 Value wrapper 的隐式类型实参也必须检查。
// expect-error: cannot hold object field 'stored'
@WrapperTarget(.Value)
wrapper SaveLocal\<T> {
    var stored: T
    pub init()
    operator .proxy.get\<TValue>(value: TValue): TValue { return value }
}
class Ref { pub init() }
pub func main(): i32 {
    @SaveLocal
    const item: Ref = new Ref()
    return 0
}
