// 隐式 TTarget/TField 代入必须检查普通字段，不可仅检查显式 W<T> 引用。
// expect-error: cannot hold object field 'hostValue'
// expect-error: cannot hold object field 'fieldValue'
@WrapperTarget(.Entity)
wrapper SaveHost\<T> { var hostValue: T }
@SaveHost
class Host { }

@WrapperTarget(.Value)
wrapper SaveField\<T> {
    var fieldValue: T
    pub init()
    operator .proxy.get\<TValue>(value: TValue): TValue { return value }
    operator .proxy.set\<TValue>(value: TValue) { inner(value) }
}
class Container\<T> {
    @SaveField
    var item: T
}
func bad(value: Container\<Object>) { }
