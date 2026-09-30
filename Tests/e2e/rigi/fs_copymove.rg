// ============================================================================
// fs_copymove.rg —— 施工块 7-6（STDLIB §4.5.7 + §4.5.8，D5）：core.fs
// 复制/移动/临时资源端到端语料。
//   ① File.copy 两模式全表：CreateNew 默认与显式形态（新目标内容一致、
//      已有目标 AlreadyExists 且内容不动）、Overwrite 打开或创建+写入
//      前截断（旧目标更长时截到源长度）、两者均不自动创建父目录、
//      复制后源不变
//   ② 自复制拒绝（§4.5.7「按实际打开的源/目标系统文件身份」）：同路
//      径 CreateNew 报 AlreadyExists（已有条目系统保证）；同路径
//      Overwrite 报 Other 且源内容原样（截断在身份判定之后——未被截
//      断即「截断前拒绝」的直接证据）。经硬链接/不同路径访问同一文件
//      的形态：本机无创建链接入口（§4.5.3 创建后置、Windows 需权限）
//      无法构造，由 7-6 fs_same_file 原语系统身份判定（Windows 卷序
//      列号+文件索引 / Linux st_dev+st_ino）承担，语料注释说明
//   ③ Overwrite 跟随目标链接写其目标文件、不替换链接条目：链接创建
//      入口后置无法构造（同②），由打开默认跟随链接 + 截断延迟到身份
//      判定之后的实现语义承担；CreateNew 对断链目标报 AlreadyExists
//      同由系统仅创建机制承担（语料注释说明）
//   ④ move（§4.5.7）：文件移动（旧条目消失、内容一致）、NoReplace 遇
//      已有目标报 AlreadyExists（两侧条目均不动）、Replace 覆盖文件、
//      Replace 对目录目标 IsDirectory（目录与源均保留——不允许目录
//      覆盖/合并）、目录移动要求目标不存在（NoReplace/Replace 两形态
//      均成功、目录内条目随迁）、目录 NoReplace 遇已有目录目标报
//      AlreadyExists、文件移入既有子目录。跨文件系统 CrossDevice：宿
//      主环境通常单卷，跨卷环境无法在本语料内稳定构造——由 fsRename
//      原语 EXDEV → CrossDevice 归一映射承担（语料注释说明）
//   ⑤ 临时文件（§4.5.8）：显式目录+prefix、名称前缀命中、原子创建返
//      回已打开输出流（写入后关闭再读回）、同 prefix 多次不冲突（互
//      异）、关闭后不自动删除（调用者删除）、prefix 含 '/' 与 NUL 报
//      InvalidPath（'\' 仅 Windows 拒绝——Linux 反斜杠是普通名称字
//      符，无平台查询 API，语料不钉平台）
//   ⑥ 临时目录（§4.5.8）：显式目录+prefix 生成、kind 为目录、多次互
//      异、prefix 含 '/' 报 InvalidPath
// 目录名取系统随机源（Random 无参构造）保证 e2e 并行与 NativeE2E 双
// 宿主各自唯一；产物全在唯一目录内、末尾自清理。
// expect-output: fs-copymove-ok
// expect-exit: 0
// ============================================================================
import core.fs.*
import core.io.Console
import core.math.*
import core.collections.*
import core.text.*

var fails: i32 = 0

func check(name: String, cond: bool) {
    if (not cond) {
        Console.println("check-FAIL ${name}")
        fails = fails + 1
    }
}

// 异常 kind 断言：body 抛出且 kind 命中 → 通过；未抛/异类 → 失败
//（单异常模型 §4.5.9：错误类别经 kind 携带，不解析错误消息）
func expectKind(name: String, want: FileSystemErrorKind,
        body: core.Action) {
    var ok = false
    try {
        body()
    } catch (e: FileSystemException) {
        if (e.kind == want) { ok = true }
    }
    check(name, ok)
}

// 期望内容：确定性周期图案（长度 n，i32 索引回绕 u8）
func makePattern(n: i32, modulus: i32): Span\<u8> {
    const buf = core.collections.spanOf\<u8>(n)
    var i: i32 = 0
    while (i < n) {
        buf[i] = ((i % modulus) as u8)
        i = (i + 1)
    }
    return buf
}

// 整文件内容与期望一致（readAll 收集后逐字节比对；读毕即关）
func fileContentEq(path: Path, expected: Span\<u8>): bool {
    const inp = File.openRead(path)
    const got = inp.readAll()
    inp.dispose()
    if (got.length != expected.length) { return false }
    var i: i32 = 0
    while (i < got.length) {
        if ((got[i] if? (0 as u8)) != (expected[i] if? (0 as u8))) {
            return false
        }
        i = (i + 1)
    }
    return true
}

func writeFile(path: Path, data: Span\<u8>) {
    const out = File.openWrite(path, .CreateOrTruncate)
    out.write(data, 0, data.length)
    out.dispose()
}

func main(): i32 {
    const rnd = new Random()
    const root = "rigi_fscopymove_probe_${rnd.nextU64()}"
    const rootP = Path.of(root)
    Directory.createAll(rootP)

    const kNF: FileSystemErrorKind = .NotFound
    const kAE: FileSystemErrorKind = .AlreadyExists
    const kID: FileSystemErrorKind = .IsDirectory
    const kIP: FileSystemErrorKind = .InvalidPath
    const kOther: FileSystemErrorKind = .Other
    const createNew: FileCopyMode = .CreateNew

    const srcP = Path.of("${root}/src.bin")
    const pat20 = makePattern(20000, 251)
    writeFile(srcP, pat20)

    // ===== ① copy：CreateNew 默认与显式 =====
    const dst1 = Path.of("${root}/dst1.bin")
    File.copy(srcP, dst1)
    check("copy-default-exists", exists(dst1, true))
    check("copy-default-content", fileContentEq(dst1, pat20))
    check("copy-source-unchanged", fileContentEq(srcP, pat20))
    const dst2 = Path.of("${root}/dst2.bin")
    File.copy(srcP, dst2, createNew)
    check("copy-createNew-content", fileContentEq(dst2, pat20))
    // 父目录不自动创建（§4.5.7）：目标父级缺失 → NotFound 且未建
    expectKind("copy-missing-parent-NF", kNF, func{() ->
        File.copy(srcP, Path.of("${root}/no_such_dir/x.bin"))})
    check("copy-missing-parent-not-created",
        not exists(Path.of("${root}/no_such_dir"), false))

    // CreateNew 对已有目标：AlreadyExists 且旧内容不动
    const old25 = makePattern(25000, 253)
    const existing = Path.of("${root}/existing.bin")
    writeFile(existing, old25)
    expectKind("copy-createNew-existing-AE", kAE, func{() ->
        File.copy(srcP, existing)})
    check("copy-createNew-existing-kept", fileContentEq(existing, old25))

    // Overwrite：已有文件写入前截断（25000 → 20000）
    File.copy(srcP, existing, .Overwrite)
    check("copy-overwrite-truncated", fileContentEq(existing, pat20))
    // Overwrite 对不存在的目标：创建
    const fresh = Path.of("${root}/fresh.bin")
    File.copy(srcP, fresh, .Overwrite)
    check("copy-overwrite-creates", fileContentEq(fresh, pat20))
    // 源缺失 → NotFound
    expectKind("copy-missing-source-NF", kNF, func{() ->
        File.copy(Path.of("${root}/missing.bin"), fresh)})
    // 源为目录 → IsDirectory（目录当文件开的宿主差异由 7-2 归一类别
    // 统一：Windows/VM 打开面拒绝，Linux 首读拒绝）
    const adir = Path.of("${root}/adir")
    Directory.create(adir)
    expectKind("copy-source-dir-ID", kID, func{() ->
        File.copy(adir, fresh)})

    // ===== ② 自复制拒绝（系统文件身份，§4.5.7）=====
    // 同路径 CreateNew：已有条目 → AlreadyExists
    expectKind("selfcopy-createNew-AE", kAE, func{() ->
        File.copy(srcP, srcP)})
    // 同路径 Overwrite：身份相同 → Other（拒绝），且源未被截断——
    // 「截断前拒绝」的直接证据（路径文本相等不作为判定面，实现按
    // 打开句柄的系统身份比较；经硬链接/不同路径的同一文件形态无法在
    // 本语料构造，见文件头②——同一原语判定承担）
    expectKind("selfcopy-overwrite-rejected", kOther, func{() ->
        File.copy(srcP, srcP, .Overwrite)})
    check("selfcopy-overwrite-no-truncate", fileContentEq(srcP, pat20))

    // ===== ④ move =====
    const pat1k = makePattern(1000, 249)
    const mvA = Path.of("${root}/mv_a.bin")
    const mvB = Path.of("${root}/mv_b.bin")
    writeFile(mvA, pat1k)
    // 默认 NoReplace：文件移动（旧条目消失、内容一致）
    move(mvA, mvB)
    check("move-file-old-gone", not exists(mvA, false))
    check("move-file-content", fileContentEq(mvB, pat1k))
    // 源缺失 → NotFound
    expectKind("move-missing-NF", kNF, func{() ->
        move(mvA, Path.of("${root}/mv_x.bin"))})
    // NoReplace 遇已有目标：AlreadyExists，两侧条目均不动
    const mvC = Path.of("${root}/mv_c.bin")
    writeFile(mvC, old25)
    expectKind("move-noReplace-existing-AE", kAE, func{() ->
        move(mvB, mvC)})
    check("move-noReplace-src-kept", fileContentEq(mvB, pat1k))
    check("move-noReplace-dst-kept", fileContentEq(mvC, old25))
    // Replace 覆盖文件：目标内容被替换、源消失
    move(mvB, mvC, .Replace)
    check("move-replace-src-gone", not exists(mvB, false))
    check("move-replace-dst-content", fileContentEq(mvC, pat1k))
    // Replace 对目录目标：IsDirectory（目录与源均保留——不允许目录
    // 覆盖或目录合并，§4.5.7）
    const mvDir = Path.of("${root}/mvdir")
    Directory.create(mvDir)
    const mvD = Path.of("${root}/mv_d.bin")
    writeFile(mvD, pat1k)
    expectKind("move-replace-dir-ID", kID, func{() ->
        move(mvD, mvDir, .Replace)})
    check("move-replace-dir-kept", exists(mvDir, true))
    check("move-replace-dir-src-kept", exists(mvD, false))
    // 目录移动要求目标不存在：两模式均成功，目录内条目随迁
    Directory.createAll(Path.of("${root}/mvsrc"))
    writeFile(Path.of("${root}/mvsrc/inner.txt"), pat1k)
    const mvDst = Path.of("${root}/mvdst")
    move(Path.of("${root}/mvsrc"), mvDst)
    check("move-dir-old-gone", not exists(Path.of("${root}/mvsrc"), false))
    check("move-dir-new-contents",
        exists(Path.of("${root}/mvdst/inner.txt"), false))
    const mvDst2 = Path.of("${root}/mvdst2")
    move(mvDst, mvDst2, .Replace)
    check("move-dir-replace-ok",
        exists(Path.of("${root}/mvdst2/inner.txt"), false))
    // 目录 NoReplace 遇已有目录目标：AlreadyExists（任何已有条目，
    // §4.5.7），两目录均保留
    const existsDir = Path.of("${root}/existsdir")
    Directory.create(existsDir)
    expectKind("move-dir-noReplace-AE", kAE, func{() ->
        move(mvDst2, existsDir)})
    check("move-dir-noReplace-kept",
        (exists(mvDst2, true)) and (exists(existsDir, true)))
    // 文件移入既有子目录（不同父级、同卷）：成功且内容一致
    Directory.create(Path.of("${root}/subdir"))
    const renamed = Path.of("${root}/subdir/renamed.bin")
    move(mvD, renamed)
    check("move-into-subdir", fileContentEq(renamed, pat1k))
    // 跨文件系统 CrossDevice：宿主环境通常单卷，跨卷无法稳定构造——
    // 由 fsRename 原语 EXDEV → CrossDevice 归一映射承担（文件头④）

    // ===== ⑤ 临时文件（§4.5.8）=====
    const pat5k = makePattern(5000, 241)
    const tf = File.createTemporary(rootP, "rigi_tmp")
    check("tempfile-name-prefix",
        tf.path.name().startsWith("rigi_tmp_"))
    check("tempfile-exists", exists(tf.path, false))
    // 返回流可写（原子创建所得已打开输出流），关闭后再读回
    tf.stream.write(pat5k, 0, pat5k.length)
    tf.stream.dispose()
    check("tempfile-content-after-close",
        fileContentEq(tf.path, pat5k))
    // 关闭不自动删除（调用者负责删除）
    check("tempfile-not-deleted-on-close", exists(tf.path, false))
    // 同 prefix 多次：互异不冲突（原子创建 + 重选名）
    const t1 = File.createTemporary(rootP, "rigi_tmp")
    const t2 = File.createTemporary(rootP, "rigi_tmp")
    const t3 = File.createTemporary(rootP, "rigi_tmp")
    check("tempfile-distinct",
        ((t1.path.text != t2.path.text)
            and (t2.path.text != t3.path.text))
            and (t1.path.text != t3.path.text))
    check("tempfile-multiple-exist",
        ((exists(t1.path, false)) and (exists(t2.path, false)))
            and (exists(t3.path, false)))
    t1.stream.dispose()
    t2.stream.dispose()
    t3.stream.dispose()
    // prefix 含 '/' → InvalidPath；含 NUL → InvalidPath（NUL 经
    // char 0 组装；'\' 仅 Windows 拒绝，Linux 反斜杠是普通名称字符，
    // 无平台查询 API，不钉平台）
    expectKind("tempfile-prefix-sep-IP", kIP, func{() ->
        File.createTemporary(rootP, "a/b")})
    const sb = new StringBuilder()
    sb.append("ri")
    sb.append((0 as char))
    sb.append("gi")
    expectKind("tempfile-prefix-nul-IP", kIP, func{() ->
        File.createTemporary(rootP, sb.toString())})

    // ===== ⑥ 临时目录（§4.5.8）=====
    const td = Directory.createTemporary(rootP, "rigi_tdir")
    check("tempdir-name-prefix",
        td.name().startsWith("rigi_tdir_"))
    const tdInfo = getInfo(td)
    const kDir: FileKind = .Directory
    check("tempdir-kind", tdInfo.kind == kDir)
    const d1 = Directory.createTemporary(rootP, "rigi_tdir")
    const d2 = Directory.createTemporary(rootP, "rigi_tdir")
    check("tempdir-distinct", d1.text != d2.text)
    check("tempdir-both-exist",
        (exists(d1, false)) and (exists(d2, false)))
    expectKind("tempdir-prefix-sep-IP", kIP, func{() ->
        Directory.createTemporary(rootP, "x/y")})

    // ===== ⑦ 清理（唯一目录名保证不残留共享名）=====
    File.delete(srcP)
    File.delete(dst1)
    File.delete(dst2)
    File.delete(existing)
    File.delete(fresh)
    File.delete(mvC)
    File.delete(renamed)
    File.delete(Path.of("${root}/mvdst2/inner.txt"))
    Directory.delete(Path.of("${root}/mvdst2"))
    Directory.delete(Path.of("${root}/subdir"))
    Directory.delete(mvDir)
    Directory.delete(existsDir)
    Directory.delete(td)
    Directory.delete(d1)
    Directory.delete(d2)
    File.delete(tf.path)
    File.delete(t1.path)
    File.delete(t2.path)
    File.delete(t3.path)
    Directory.delete(adir)
    Directory.delete(rootP)
    check("cleanup-root-gone", not exists(rootP, false))
    if (fails == 0) {
        Console.println("fs-copymove-ok")
    }
    return fails
}
