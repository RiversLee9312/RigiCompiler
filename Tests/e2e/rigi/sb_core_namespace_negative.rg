// 不能通过加入标准库命名空间来取得 SB 权限。
// expect-error: reserved for the compiler standard library
namespace core.collections.forged
@SerializationBase
class Forged { pub init() }
pub func main(): i32 { return 0 }
