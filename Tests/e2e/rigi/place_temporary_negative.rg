// expect-error: placeOf requires stable value storage
pub func main(): i32 {
    const p = placeOf (1 + 2)
    return 0
}
