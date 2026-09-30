/*
 * rigi_rt math 原语库（施工块 6-4，STDLIB §4.11.1–§4.11.3 / D7）——
 * stdlib core/math.rg 的 priv native 原语族（@NativeSymbol("math_*")，
 * C 导出 rigi_math_*）的 C 侧实现：libm 调用，f32/f64 独立精度路径
 * （float 走 *f 函数族，不许先 double 再截）。
 *
 * 语义契约（§4.11.3，VM 侧 VmHooks 对应 Math/MathF 镜像同规则）：
 *   - 取整（floor/ceil/trunc/rint/round）是 IEEE 确定运算，两端精确一致；
 *   - sqrt 按目标精度最近值、正中取偶正确舍入（sqrt/sqrtf 均 IEEE 保证）；
 *   - 超越函数（pow/exp/log 族/三角/反三角/atan2）有限结果误差 ≤ 4 ULP
 *     （以目标格式正确舍入参考值处间距度量），NaN/无穷/±0 分类与符号
 *     按 IEEE 规则（负有限 sqrt→NaN、负 log→NaN、零 log→-inf、
 *     |x|>1 的 asin/acos→NaN、inf 输入 sin/cos/tan→NaN、
 *     atan2 保留 ±0 象限、pow(0,0)=pow(NaN,0)=pow(1,NaN)=1 …）；
 *   - 不把宿主 errno / 硬件浮点状态变成 Rigi 异常。
 *
 * libm 链接：Windows 数学函数在 ucrt（lld 默认链接）；Linux 现代 glibc
 * 数学符号并入 libc，NativeCommand 另加 -lm 兜底旧发行版。
 */
#include <math.h>
#include <stdint.h>

/* ===== 取整族（§4.11.2：floor 向负无穷 / ceil 向正无穷 / trunc 向零 /
 *       round ToEven=rint（默认舍入模式最近取偶）/ AwayFromZero=round） ===== */

float rigi_math_floor_f32(float x) { return floorf(x); }
double rigi_math_floor_f64(double x) { return floor(x); }

float rigi_math_ceil_f32(float x) { return ceilf(x); }
double rigi_math_ceil_f64(double x) { return ceil(x); }

float rigi_math_trunc_f32(float x) { return truncf(x); }
double rigi_math_trunc_f64(double x) { return trunc(x); }

float rigi_math_round_even_f32(float x) { return rintf(x); }
double rigi_math_round_even_f64(double x) { return rint(x); }

float rigi_math_round_away_f32(float x) { return roundf(x); }
double rigi_math_round_away_f64(double x) { return round(x); }

/* ===== sqrt / pow / 超越函数（§4.11.3）=====
 * sqrt/sqrtf 为 IEEE 正确舍入（正中取偶）；glibc/ucrt 的 libm 超越
 * 函数满足 ≤4 ULP 口径；特殊值分类（负 sqrt→NaN、负 log→NaN、
 * 零 log→-inf、|x|>1 asin/acos→NaN、inf 输入 sin/cos/tan→NaN、
 * atan2 ±0 象限、pow(0,0)=pow(NaN,0)=pow(1,NaN)=1、负底非整数指→NaN）
 * 由 libm 的 IEEE 754-2008 约定承担，VM 侧 Math/MathF 同规则镜像；
 * 独立语料（math_basic.rg）逐条对拍，不比较两端碰巧相同。 */

float rigi_math_sqrt_f32(float x) { return sqrtf(x); }
double rigi_math_sqrt_f64(double x) { return sqrt(x); }

/* ===== pow（施工块 6-4，§4.11.3 固定规则）=====
 * Windows ucrt / .NET Math.Pow 宿主实现误差可超 4 ULP（实测
 * pow(0.99,1000) 距正确舍入 57 ULP），不满足契约——按 D7「修正底层
 * 实现，不放宽契约」移植 fdlibm（FreeBSD libm 现行 e_pow.c/e_powf.c，
 * 含 IEEE 754-2008 的 +-1**+-INF=1 修正）：
 *
 *   Method:  Let x = 2^n * (1+f)
 *   1. Compute log2(x) in two pieces (w1+w2, w1 has 29 trailing zeros)
 *   2. y*log2(x) = n+y' by multi-precision simulation (|y'|<=0.5)
 *   3. x**y = 2**n * exp(y'*log2)
 *
 * 特殊值表（与 §4.11.3 语料 math_basic.rg pow 节逐条对拍）：
 *   (anything)**0=1；1**NaN=1；NaN**(非0)=NaN；(±1)**±INF=1；
 *   |x|>1 ** +INF=+INF；-INF=+0；|x|<1 反之；±0 ** 正=±0（奇整数取负号）、
 *   ** 负=±INF；-INF**(anything)=-(0**-anything)；负底非整数指=NaN。
 *
 * 原版权_notice（须保留）：
 *   Copyright (C) 2004 by Sun Microsystems, Inc. All rights reserved.
 *   Permission to use, copy, modify, and distribute this software is
 *   freely granted, provided that this notice is preserved.
 *
 * 移植说明：EXTRACT_WORDS/SET_*_WORD 换成本文件位助手；nan_mix 用
 * x+y（本库不承诺 NaN 载荷）；scalbn 自实现（n 界内，避免额外 libm
 * 依赖）。float 版为 e_powf.c 同款（Ian Lance Taylor 的 float 转换，
 * Copyright (C) 1993 by Sun Microsystems, Inc.）。 */

static inline uint64_t rigi_math_dbits(double x)
{
    union { double d; uint64_t u; } v;
    v.d = x;
    return v.u;
}

static inline double rigi_math_dfrom(uint64_t u)
{
    union { double d; uint64_t u; } v;
    v.u = u;
    return v.d;
}

static inline uint32_t rigi_math_fbits(float x)
{
    union { float f; uint32_t u; } v;
    v.f = x;
    return v.u;
}

static inline float rigi_math_ffrom(uint32_t u)
{
    union { float f; uint32_t u; } v;
    v.u = u;
    return v.f;
}

/* scalbn(z,n)：z∈(0,4) 且 |n|≲1200 的 pow 内部使用形态；溢出/下溢
 * 按 IEEE 得 ±inf / ±0；次正规结果经 2^53 预缩放路径。 */
static double rigi_math_scalbn_f64(double x, int n)
{
    uint64_t mant;
    int e, ne;
    if (x != x) { return x; }
    if (x == 0.0) { return x; }
    mant = rigi_math_dbits(x);
    e = (int)((mant >> 52) & 0x7ffu);
    if (e == 0x7ff) { return x; }
    if (e == 0)
    {
        x = x * 9007199254740992.0; /* 2^53 */
        mant = rigi_math_dbits(x);
        e = (int)((mant >> 52) & 0x7ffu) - 53;
    }
    else
    {
        e -= 1023;
    }
    ne = e + n;
    mant &= 0x800fffffffffffffULL;
    if (ne > 1023) { return (x < 0.0) ? -INFINITY : INFINITY; }
    if (ne < -1074) { return (x < 0.0) ? -0.0 : 0.0; }
    if (ne < -1022)
    {
        mant |= (uint64_t)(ne + 1023 + 53) << 52;
        return rigi_math_dfrom(mant) * 1.1102230246251565e-16; /* 2^-53 */
    }
    mant |= (uint64_t)(ne + 1023) << 52;
    return rigi_math_dfrom(mant);
}

static float rigi_math_scalbn_f32(float x, int n)
{
    uint32_t mant;
    int e, ne;
    if (x != x) { return x; }
    if (x == 0.0f) { return x; }
    mant = rigi_math_fbits(x);
    e = (int)((mant >> 23) & 0xffu);
    if (e == 0xff) { return x; }
    if (e == 0)
    {
        x = x * 16777216.0f; /* 2^24 */
        mant = rigi_math_fbits(x);
        e = (int)((mant >> 23) & 0xffu) - 24;
    }
    else
    {
        e -= 127;
    }
    ne = e + n;
    mant &= 0x807fffffu;
    if (ne > 127) { return (x < 0.0f) ? -INFINITY : INFINITY; }
    if (ne < -149) { return (x < 0.0f) ? -0.0f : 0.0f; }
    if (ne < -126)
    {
        mant |= (uint32_t)(ne + 127 + 24) << 23;
        return rigi_math_ffrom(mant) * 5.9604644775390625e-8f; /* 2^-24 */
    }
    mant |= (uint32_t)(ne + 127) << 23;
    return rigi_math_ffrom(mant);
}

double rigi_math_pow_f64(double x, double y)
{
    static const double
    bp[] = {1.0, 1.5,},
    dp_h[] = { 0.0, 5.84962487220764160156e-01,},
    dp_l[] = { 0.0, 1.35003920212974897128e-08,},
    zero    =  0.0,
    half    =  0.5,
    qrtr    =  0.25,
    thrd    =  3.3333333333333331e-01,
    one	=  1.0,
    two	=  2.0,
    two53	=  9007199254740992.0,
    huge	=  1.0e300,
    tiny    =  1.0e-300,
    L1  =  5.99999999999994648725e-01,
    L2  =  4.28571428578550184252e-01,
    L3  =  3.33333329818377432918e-01,
    L4  =  2.72728123808534006489e-01,
    L5  =  2.30660745775561754067e-01,
    L6  =  2.06975017800338417784e-01,
    P1   =  1.66666666666666019037e-01,
    P2   = -2.77777777770155933842e-03,
    P3   =  6.61375632143793436117e-05,
    P4   = -1.65339022054652515390e-06,
    P5   =  4.13813679705723846039e-08,
    lg2  =  6.93147180559945286227e-01,
    lg2_h  =  6.93147182464599609375e-01,
    lg2_l  = -1.90465429995776804525e-09,
    ovt =  8.0085662595372944372e-0017,
    cp    =  9.61796693925975554329e-01,
    cp_h  =  9.61796700954437255859e-01,
    cp_l  = -7.02846165095275826516e-09,
    ivln2    =  1.44269504088896338700e+00,
    ivln2_h  =  1.44269502162933349609e+00,
    ivln2_l  =  1.92596299112661746887e-08;

    double z,ax,z_h,z_l,p_h,p_l;
    double y1,t1,t2,r,s,t,u,v,w;
    int i,j,k,yisint,n;
    int32_t hx,hy,ix,iy;
    uint32_t lx,ly;
    uint64_t xb, yb;

    xb = rigi_math_dbits(x);
    hx = (int32_t)(xb >> 32); lx = (uint32_t)xb;
    yb = rigi_math_dbits(y);
    hy = (int32_t)(yb >> 32); ly = (uint32_t)yb;
    ix = hx&0x7fffffff;  iy = hy&0x7fffffff;

    /* y==zero: x**0 = 1 */
    if(((uint32_t)iy|ly)==0) return one;

    /* x==1: 1**y = 1, even if y is NaN */
    if (hx==0x3ff00000 && lx == 0) return one;

    /* y!=zero: result is NaN if either arg is NaN */
    if((uint32_t)ix > 0x7ff00000u || ((uint32_t)ix==0x7ff00000u && lx!=0) ||
       (uint32_t)iy > 0x7ff00000u || ((uint32_t)iy==0x7ff00000u && ly!=0))
        return x + y;

    /* determine if y is an odd int when x < 0 */
    yisint  = 0;
    if(hx<0) {
        if((uint32_t)iy>=0x43400000u) yisint = 2; /* even integer y */
        else if((uint32_t)iy>=0x3ff00000u) {
            k = (iy>>20)-0x3ff;	   /* exponent */
            if(k>20) {
                j = (int)(ly>>(52-k));
                if(((uint32_t)j<<(52-k))==ly) yisint = 2-(j&1);
            } else if(ly==0) {
                j = (iy>>(20-k));
                if((j<<(20-k))==iy) yisint = 2-(j&1);
            }
        }
    }

    /* special value of y */
    if(ly==0) {
        if ((uint32_t)iy==0x7ff00000u) {	/* y is +-inf */
            if((((uint32_t)ix-0x3ff00000u)|lx)==0)
                return  one;	/* (-1)**+-inf is 1 */
            else if ((uint32_t)ix >= 0x3ff00000u)/* (|x|>1)**+-inf = inf,0 */
                return (hy>=0)? y: zero;
            else			/* (|x|<1)**-,+inf = inf,0 */
                return (hy<0)?-y: zero;
        }
        if(iy==0x3ff00000) {	/* y is  +-1 */
            if(hy<0) return one/x; else return x;
        }
        if(hy==0x40000000) return x*x; /* y is  2 */
        if(hy==0x3fe00000) {	/* y is  0.5 */
            if(hx>=0)	/* x >= +0 */
            return sqrt(x);
        }
    }

    ax   = fabs(x);
    /* special value of x */
    if(lx==0) {
        if(ix==0x7ff00000||ix==0||ix==0x3ff00000){
            z = ax;			/*x is +-0,+-inf,+-1*/
            if(hy<0) z = one/z;	/* z = (1/|x|) */
            if(hx<0) {
                if((((uint32_t)ix-0x3ff00000u)|(uint32_t)yisint)==0) {
                    z = (z-z)/(z-z); /* (-1)**non-int is NaN */
                } else if(yisint==1)
                    z = -z;		/* (x<0)**odd = -(|x|**odd) */
            }
            return z;
        }
    }

    n = (int)(((uint32_t)hx>>31)-1u);

    /* (x<0)**(non-int) is NaN */
    if((n|yisint)==0) return (x-x)/(x-x);

    s = one; /* s (sign of result -ve**odd) = -1 else = 1 */
    if((n|(yisint-1))==0) s = -one;/* (-ve)**(odd int) */

    /* |y| is huge */
    if((uint32_t)iy>0x41e00000u) { /* if |y| > 2**31 */
        if((uint32_t)iy>0x43f00000u){	/* if |y| > 2**64, must o/uflow */
            if((uint32_t)ix<=0x3fefffffu) return (hy<0)? huge*huge:tiny*tiny;
            if((uint32_t)ix>=0x3ff00000u) return (hy>0)? huge*huge:tiny*tiny;
        }
        /* over/underflow if x is not close to one */
        if((uint32_t)ix<0x3fefffffu) return (hy<0)? s*huge*huge:s*tiny*tiny;
        if((uint32_t)ix>0x3ff00000u) return (hy>0)? s*huge*huge:s*tiny*tiny;
        /* now |1-x| is tiny <= 2**-20, suffice to compute
           log(x) by x-x^2/2+x^3/3-x^4/4 */
        t = ax-one;		/* t has 20 trailing zeros */
        w = (t*t)*(half-t*(thrd-t*qrtr));
        u = ivln2_h*t;	/* ivln2_h has 21 sig. bits */
        v = t*ivln2_l-w*ivln2;
        t1 = u+v;
        t1 = rigi_math_dfrom(rigi_math_dbits(t1) & 0xffffffff00000000ULL);
        t2 = v-(t1-u);
    } else {
        double ss,s2,s_h,s_l,t_h,t_l;
        n = 0;
        /* take care subnormal number */
        if((uint32_t)ix<0x00100000u)
            {ax *= two53; n -= 53; ix = (int32_t)(rigi_math_dbits(ax) >> 32); }
        n  += (ix>>20)-0x3ff;
        j  = ix&0x000fffff;
        /* determine interval */
        ix = j|0x3ff00000;		/* normalize ix */
        if(j<=0x3988E) k=0;		/* |x|<sqrt(3/2) */
        else if(j<0xBB67A) k=1;	/* |x|<sqrt(3)   */
        else {k=0;n+=1;ix -= 0x00100000;}
        ax = rigi_math_dfrom((rigi_math_dbits(ax) & 0xffffffffULL)
            | ((uint64_t)(uint32_t)ix << 32));

        /* compute ss = s_h+s_l = (x-1)/(x+1) or (x-1.5)/(x+1.5) */
        u = ax-bp[k];		/* bp[0]=1.0, bp[1]=1.5 */
        v = one/(ax+bp[k]);
        ss = u*v;
        s_h = ss;
        s_h = rigi_math_dfrom(rigi_math_dbits(s_h) & 0xffffffff00000000ULL);
        /* t_h=ax+bp[k] High */
        t_h = zero;
        t_h = rigi_math_dfrom((uint64_t)(uint32_t)(((ix>>1)|0x20000000)
            +0x00080000+(k<<18)) << 32);
        t_l = ax - (t_h-bp[k]);
        s_l = v*((u-s_h*t_h)-s_h*t_l);
        /* compute log(ax) */
        s2 = ss*ss;
        r = s2*s2*(L1+s2*(L2+s2*(L3+s2*(L4+s2*(L5+s2*L6)))));
        r += s_l*(s_h+ss);
        s2  = s_h*s_h;
        t_h = 3+s2+r;
        t_h = rigi_math_dfrom(rigi_math_dbits(t_h) & 0xffffffff00000000ULL);
        t_l = r-((t_h-3)-s2);
        /* u+v = ss*(1+...) */
        u = s_h*t_h;
        v = s_l*t_h+t_l*ss;
        /* 2/(3log2)*(ss+...) */
        p_h = u+v;
        p_h = rigi_math_dfrom(rigi_math_dbits(p_h) & 0xffffffff00000000ULL);
        p_l = v-(p_h-u);
        z_h = cp_h*p_h;		/* cp_h+cp_l = 2/(3*log2) */
        z_l = cp_l*p_h+p_l*cp+dp_l[k];
        /* log2(ax) = (ss+..)*2/(3*log2) = n + dp_h + z_h + z_l */
        t = n;
        t1 = (((z_h+z_l)+dp_h[k])+t);
        t1 = rigi_math_dfrom(rigi_math_dbits(t1) & 0xffffffff00000000ULL);
        t2 = z_l-(((t1-t)-dp_h[k])-z_h);
    }

    /* split up y into y1+y2 and compute (y1+y2)*(t1+t2) */
    y1  = y;
    y1 = rigi_math_dfrom(rigi_math_dbits(y1) & 0xffffffff00000000ULL);
    p_l = (y-y1)*t1+y*t2;
    p_h = y1*t1;
    z = p_l+p_h;
    j = (int32_t)(rigi_math_dbits(z) >> 32);
    i = (int32_t)(uint32_t)rigi_math_dbits(z);
    if (j>=0x40900000) {				/* z >= 1024 */
        if((((uint32_t)j-0x40900000u)|(uint32_t)i)!=0)			/* if z > 1024 */
            return s*huge*huge;			/* overflow */
        else {
            if(p_l+ovt>z-p_h) return s*huge*huge;	/* overflow */
        }
    } else if((j&0x7fffffff)>=0x4090cc00 ) {	/* z <= -1075 */
        if((((uint32_t)j-0xc090cc00u)|(uint32_t)i)!=0) 		/* z < -1075 */
            return s*tiny*tiny;		/* underflow */
        else {
            if(p_l<=z-p_h) return s*tiny*tiny;	/* underflow */
        }
    }
    /*
     * compute 2**(p_h+p_l)
     */
    i = j&0x7fffffff;
    k = (i>>20)-0x3ff;
    n = 0;
    if(i>0x3fe00000) {		/* if |z| > 0.5, set n = [z+0.5] */
        n = j+(0x00100000>>(k+1));
        k = ((n&0x7fffffff)>>20)-0x3ff;	/* new k for n */
        t = zero;
        t = rigi_math_dfrom((uint64_t)(uint32_t)(n&~(0x000fffff>>k)) << 32);
        n = ((n&0x000fffff)|0x00100000)>>(20-k);
        if(j<0) n = -n;
        p_h -= t;
    }
    t = p_l+p_h;
    t = rigi_math_dfrom(rigi_math_dbits(t) & 0xffffffff00000000ULL);
    u = t*lg2_h;
    v = (p_l-(t-p_h))*lg2+t*lg2_l;
    z = u+v;
    w = v-(z-u);
    t  = z*z;
    t1  = z - t*(P1+t*(P2+t*(P3+t*(P4+t*P5))));
    r  = (z*t1)/(t1-two)-(w+z*w);
    z  = one-(r-z);
    j = (int32_t)(rigi_math_dbits(z) >> 32);
    /*
     * sign bit of z is 0.
     * sign bit of j will indicate sign of 0x3ff-biased exponent.
     */
    j += (int32_t)((uint32_t)n<<20);
    if((j>>20)<=0) z = rigi_math_scalbn_f64(z,n);	/* subnormal output */
    else z = rigi_math_dfrom((rigi_math_dbits(z) & 0xffffffffULL)
        | ((uint64_t)(uint32_t)j << 32));
    return s*z;
}

float rigi_math_pow_f32(float x, float y)
{
    static const float
    bp[] = {1.0, 1.5,},
    dp_h[] = { 0.0, 5.84960938e-01,},
    dp_l[] = { 0.0, 1.56322085e-06,},
    zero    =  0.0,
    half    =  0.5,
    qrtr    =  0.25,
    thrd    =  3.33333343e-01,
    one	=  1.0,
    two	=  2.0,
    two24	=  16777216.0,
    huge	=  1.0e30,
    tiny    =  1.0e-30,
    L1  =  6.0000002384e-01,
    L2  =  4.2857143283e-01,
    L3  =  3.3333334327e-01,
    L4  =  2.7272811532e-01,
    L5  =  2.3066075146e-01,
    L6  =  2.0697501302e-01,
    P1   =  1.6666667163e-01,
    P2   = -2.7777778450e-03,
    P3   =  6.6137559770e-05,
    P4   = -1.6533901999e-06,
    P5   =  4.1381369442e-08,
    lg2  =  6.9314718246e-01,
    lg2_h  =  6.93145752e-01,
    lg2_l  =  1.42860654e-06,
    ovt =  4.2995665694e-08,
    cp    =  9.6179670095e-01,
    cp_h  =  9.6191406250e-01,
    cp_l  = -1.1736857402e-04,
    ivln2    =  1.4426950216e+00,
    ivln2_h  =  1.4426879883e+00,
    ivln2_l  =  7.0526075433e-06;

    float z,ax,z_h,z_l,p_h,p_l;
    float y1,t1,t2,r,s,sn,t,u,v,w;
    int i,j,k,yisint,n;
    int32_t hx,hy,ix,iy,is;

    hx = (int32_t)rigi_math_fbits(x);
    hy = (int32_t)rigi_math_fbits(y);
    ix = hx&0x7fffffff;  iy = hy&0x7fffffff;

    /* y==zero: x**0 = 1 */
    if(iy==0) return one;

    /* x==1: 1**y = 1, even if y is NaN */
    if (hx==0x3f800000) return one;

    /* y!=zero: result is NaN if either arg is NaN */
    if((uint32_t)ix > 0x7f800000u ||
       (uint32_t)iy > 0x7f800000u)
        return x + y;

    /* determine if y is an odd int when x < 0 */
    yisint  = 0;
    if(hx<0) {
        if((uint32_t)iy>=0x4b800000u) yisint = 2; /* even integer y */
        else if((uint32_t)iy>=0x3f800000u) {
            k = (iy>>23)-0x7f;	   /* exponent */
            j = (int)((uint32_t)iy>>(23-k));
            if((j<<(23-k))==iy) yisint = 2-(j&1);
        }
    }

    /* special value of y */
    if ((uint32_t)iy==0x7f800000u) {	/* y is +-inf */
        if ((uint32_t)ix==0x3f800000u)
            return  one;	/* (-1)**+-inf is 1 */
        else if ((uint32_t)ix > 0x3f800000u)/* (|x|>1)**+-inf = inf,0 */
            return (hy>=0)? y: zero;
        else			/* (|x|<1)**-,+inf = inf,0 */
            return (hy<0)?-y: zero;
    }
    if(iy==0x3f800000) {	/* y is  +-1 */
        if(hy<0) return one/x; else return x;
    }
    if(hy==0x40000000) return x*x; /* y is  2 */
    if(hy==0x3f000000) {	/* y is  0.5 */
        if(hx>=0)	/* x >= +0 */
        return sqrtf(x);
    }

    ax   = fabsf(x);
    /* special value of x */
    if(ix==0x7f800000||ix==0||ix==0x3f800000){
        z = ax;			/*x is +-0,+-inf,+-1*/
        if(hy<0) z = one/z;	/* z = (1/|x|) */
        if(hx<0) {
            if((((uint32_t)ix-0x3f800000u)|(uint32_t)yisint)==0) {
                z = (z-z)/(z-z); /* (-1)**non-int is NaN */
            } else if(yisint==1)
                z = -z;		/* (x<0)**odd = -(|x|**odd) */
        }
        return z;
    }

    n = (int)(((uint32_t)hx>>31)-1u);

    /* (x<0)**(non-int) is NaN */
    if((n|yisint)==0) return (x-x)/(x-x);

    sn = one; /* s (sign of result -ve**odd) = -1 else = 1 */
    if((n|(yisint-1))==0) sn = -one;/* (-ve)**(odd int) */

    /* |y| is huge */
    if((uint32_t)iy>0x4d000000u) { /* if |y| > 2**27 */
        /* over/underflow if x is not close to one */
        if((uint32_t)ix<0x3f7ffff6u) return (hy<0)? sn*huge*huge:sn*tiny*tiny;
        if((uint32_t)ix>0x3f800007u) return (hy>0)? sn*huge*huge:sn*tiny*tiny;
        /* now |1-x| is tiny <= 2**-20, suffice to compute
           log(x) by x-x^2/2+x^3/3-x^4/4 */
        t = ax-1;		/* t has 20 trailing zeros */
        w = (t*t)*(half-t*(thrd-t*qrtr));
        u = ivln2_h*t;	/* ivln2_h has 16 sig. bits */
        v = t*ivln2_l-w*ivln2;
        t1 = u+v;
        is = (int32_t)rigi_math_fbits(t1);
        t1 = rigi_math_ffrom((uint32_t)is & 0xfffff000u);
        t2 = v-(t1-u);
    } else {
        float s2,s_h,s_l,t_h,t_l;
        n = 0;
        /* take care subnormal number */
        if((uint32_t)ix<0x00800000u)
            {ax *= two24; n -= 24; ix = (int32_t)rigi_math_fbits(ax); }
        n  += (ix>>23)-0x7f;
        j  = ix&0x007fffff;
        /* determine interval */
        ix = j|0x3f800000;		/* normalize ix */
        if(j<=0x1cc471) k=0;		/* |x|<sqrt(3/2) */
        else if(j<0x5db3d7) k=1;	/* |x|<sqrt(3)   */
        else {k=0;n+=1;ix -= 0x00800000;}
        ax = rigi_math_ffrom((uint32_t)ix);

        /* compute s = s_h+s_l = (x-1)/(x+1) or (x-1.5)/(x+1.5) */
        u = ax-bp[k];		/* bp[0]=1.0, bp[1]=1.5 */
        v = one/(ax+bp[k]);
        s = u*v;
        s_h = s;
        is = (int32_t)rigi_math_fbits(s_h);
        s_h = rigi_math_ffrom((uint32_t)is & 0xfffff000u);
        /* t_h=ax+bp[k] High */
        is = (int32_t)(((uint32_t)ix>>1)&0xfffff000u)|0x20000000;
        t_h = rigi_math_ffrom((uint32_t)is+0x00400000u+(uint32_t)(k<<21));
        t_l = ax - (t_h-bp[k]);
        s_l = v*((u-s_h*t_h)-s_h*t_l);
        /* compute log(ax) */
        s2 = s*s;
        r = s2*s2*(L1+s2*(L2+s2*(L3+s2*(L4+s2*(L5+s2*L6)))));
        r += s_l*(s_h+s);
        s2  = s_h*s_h;
        t_h = 3+s2+r;
        is = (int32_t)rigi_math_fbits(t_h);
        t_h = rigi_math_ffrom((uint32_t)is & 0xfffff000u);
        t_l = r-((t_h-3)-s2);
        /* u+v = s*(1+...) */
        u = s_h*t_h;
        v = s_l*t_h+t_l*s;
        /* 2/(3log2)*(s+...) */
        p_h = u+v;
        is = (int32_t)rigi_math_fbits(p_h);
        p_h = rigi_math_ffrom((uint32_t)is & 0xfffff000u);
        p_l = v-(p_h-u);
        z_h = cp_h*p_h;		/* cp_h+cp_l = 2/(3*log2) */
        z_l = cp_l*p_h+p_l*cp+dp_l[k];
        /* log2(ax) = (s+..)*2/(3*log2) = n + dp_h + z_h + z_l */
        t = n;
        t1 = (((z_h+z_l)+dp_h[k])+t);
        is = (int32_t)rigi_math_fbits(t1);
        t1 = rigi_math_ffrom((uint32_t)is & 0xfffff000u);
        t2 = z_l-(((t1-t)-dp_h[k])-z_h);
    }

    /* split up y into y1+y2 and compute (y1+y2)*(t1+t2) */
    is = (int32_t)rigi_math_fbits(y);
    y1 = rigi_math_ffrom((uint32_t)is & 0xfffff000u);
    p_l = (y-y1)*t1+y*t2;
    p_h = y1*t1;
    z = p_l+p_h;
    j = (int32_t)rigi_math_fbits(z);
    if (j>0x43000000)				/* if z > 128 */
        return sn*huge*huge;			/* overflow */
    else if (j==0x43000000) {			/* if z == 128 */
        if(p_l+ovt>z-p_h) return sn*huge*huge;	/* overflow */
    }
    else if ((j&0x7fffffff)>0x43160000)		/* z <= -150 */
        return sn*tiny*tiny;			/* underflow */
    else if (j==0xc3160000){			/* z == -150 */
        if(p_l<=z-p_h) return sn*tiny*tiny;		/* underflow */
    }
    /*
     * compute 2**(p_h+p_l)
     */
    i = j&0x7fffffff;
    k = (i>>23)-0x7f;
    n = 0;
    if(i>0x3f000000) {		/* if |z| > 0.5, set n = [z+0.5] */
        n = j+(0x00800000>>(k+1));
        k = ((n&0x7fffffff)>>23)-0x7f;	/* new k for n */
        t = rigi_math_ffrom((uint32_t)(n&~(0x007fffff>>k)));
        n = ((n&0x007fffff)|0x00800000)>>(23-k);
        if(j<0) n = -n;
        p_h -= t;
    }
    t = p_l+p_h;
    is = (int32_t)rigi_math_fbits(t);
    t = rigi_math_ffrom((uint32_t)is & 0xffff8000u);
    u = t*lg2_h;
    v = (p_l-(t-p_h))*lg2+t*lg2_l;
    z = u+v;
    w = v-(z-u);
    t  = z*z;
    t1  = z - t*(P1+t*(P2+t*(P3+t*(P4+t*P5))));
    r  = (z*t1)/(t1-two)-(w+z*w);
    z  = one-(r-z);
    j = (int32_t)rigi_math_fbits(z);
    /*
     * sign bit of z is 0.
     * sign bit of j will indicate sign of 0x7f-biased exponent.
     */
    j += (int32_t)((uint32_t)n<<23);
    if((j>>23)<=0) z = rigi_math_scalbn_f32(z,n);	/* subnormal output */
    else z = rigi_math_ffrom((uint32_t)j);
    return sn*z;
}

float rigi_math_exp_f32(float x) { return expf(x); }
double rigi_math_exp_f64(double x) { return exp(x); }

float rigi_math_ln_f32(float x) { return logf(x); }
double rigi_math_ln_f64(double x) { return log(x); }

float rigi_math_log2_f32(float x) { return log2f(x); }
double rigi_math_log2_f64(double x) { return log2(x); }

float rigi_math_log10_f32(float x) { return log10f(x); }
double rigi_math_log10_f64(double x) { return log10(x); }

float rigi_math_sin_f32(float x) { return sinf(x); }
double rigi_math_sin_f64(double x) { return sin(x); }

float rigi_math_cos_f32(float x) { return cosf(x); }
double rigi_math_cos_f64(double x) { return cos(x); }

float rigi_math_tan_f32(float x) { return tanf(x); }
double rigi_math_tan_f64(double x) { return tan(x); }

float rigi_math_asin_f32(float x) { return asinf(x); }
double rigi_math_asin_f64(double x) { return asin(x); }

float rigi_math_acos_f32(float x) { return acosf(x); }
double rigi_math_acos_f64(double x) { return acos(x); }

float rigi_math_atan_f32(float x) { return atanf(x); }
double rigi_math_atan_f64(double x) { return atan(x); }

float rigi_math_atan2_f32(float y, float x) { return atan2f(y, x); }
double rigi_math_atan2_f64(double y, double x) { return atan2(y, x); }
