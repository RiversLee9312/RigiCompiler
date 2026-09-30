// B2-4a：Span<u8> native ABI 端到端（span_u8_echo XOR 回声原语，
// stdlib core.native 的 spanU8Echo 公开包装）——spanOf<u8> 填已知字节，
// 全范围 XOR 断言翻转与返回值；再对部分区间 XOR 一次还原重叠区
//（XOR 两次 = 原值）并断言返回处理字节数
// expect-exit: 0
import core.collections.*
import core.native.*

pub func main(): i32 {
    var b = spanOf\<u8>(4)
    b[0] = 18UB
    b[1] = 255UB
    b[2] = 0UB
    b[3] = 165UB
    // 全范围翻转：返回处理字节数 = 4
    var n = spanU8Echo(b, 0, 4)
    if (n != 4) { return 1 }
    // 18^255=237, 255^255=0, 0^255=255, 165^255=90
    if ((b[0] if? 0UB) != 237UB) { return 2 }
    if ((b[1] if? 0UB) != 0UB) { return 3 }
    if ((b[2] if? 0UB) != 255UB) { return 4 }
    if ((b[3] if? 0UB) != 90UB) { return 5 }
    // 部分区间 [1,2)：重叠区 XOR 两次 = 原值（还原），区间外不动
    var m = spanU8Echo(b, 1, 2)
    if (m != 2) { return 6 }
    if ((b[0] if? 0UB) != 237UB) { return 7 }
    if ((b[1] if? 0UB) != 255UB) { return 8 }
    if ((b[2] if? 0UB) != 0UB) { return 9 }
    if ((b[3] if? 0UB) != 90UB) { return 10 }
    // count == 0：合法空操作，返回 0
    var z = spanU8Echo(b, 2, 0)
    if (z != 0) { return 11 }
    if ((b[2] if? 0UB) != 0UB) { return 12 }
    return 0
}
