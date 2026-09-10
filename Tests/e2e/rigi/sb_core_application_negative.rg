// core 隐式可见不等于获得 Internal 应用权限。
// expect-error: 'SerializationBase' is internal and cannot be applied
@SerializationBase
class Forged { pub init() }
pub func main(): i32 { return 0 }
