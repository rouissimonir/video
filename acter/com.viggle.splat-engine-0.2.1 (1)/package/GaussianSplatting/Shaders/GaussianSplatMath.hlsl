// SPDX-License-Identifier: MIT
// Buffer-free splat maths, shared by every render path.
//
// Extracted verbatim from GaussianSplatting.hlsl (quaternion helpers, 3D covariance from
// rot/scale, the EWA 2D projection) and SplatUtilities.compute (DecomposeCovariance), so the
// no-compute path can reuse the exact same maths. That path runs on GLES 3.0, where neither of
// those files can be included: the umbrella header declares StructuredBuffers from
// _SplatChunks onwards, and a .compute cannot be included by a vertex shader at all.
//
// Nothing here may declare a buffer or a texture. Keep it pure functions of its arguments.
#ifndef GAUSSIAN_SPLAT_MATH_HLSL
#define GAUSSIAN_SPLAT_MATH_HLSL

float InvSquareCentered01(float x)
{
    x -= 0.5;
    x *= 0.5;
    x = sqrt(abs(x)) * sign(x);
    return x + 0.5;
}

float3 QuatRotateVector(float3 v, float4 r)
{
    float3 t = 2 * cross(r.xyz, v);
    return v + r.w * t + cross(r.xyz, t);
}

float4 QuatMul(float4 a, float4 b)
{
    return float4(a.wwww * b + (a.xyzx * b.wwwx + a.yzxy * b.zxyy) * float4(1,1,1,-1) - a.zxyz * b.yzxz);
}

float4 QuatInverse(float4 q)
{
    return rcp(dot(q, q)) * q * float4(-1,-1,-1,1);
}

float3x3 CalcMatrixFromRotationScale(float4 rot, float3 scale)
{
    float3x3 ms = float3x3(
        scale.x, 0, 0,
        0, scale.y, 0,
        0, 0, scale.z
    );
    float x = rot.x;
    float y = rot.y;
    float z = rot.z;
    float w = rot.w;
    float3x3 mr = float3x3(
        1-2*(y*y + z*z),   2*(x*y - w*z),   2*(x*z + w*y),
          2*(x*y + w*z), 1-2*(x*x + z*z),   2*(y*z - w*x),
          2*(x*z - w*y),   2*(y*z + w*x), 1-2*(x*x + y*y)
    );
    return mul(mr, ms);
}

void CalcCovariance3D(float3x3 rotMat, out float3 sigma0, out float3 sigma1)
{
    float3x3 sig = mul(rotMat, transpose(rotMat));
    sigma0 = float3(sig._m00, sig._m01, sig._m02);
    sigma1 = float3(sig._m11, sig._m12, sig._m22);
}

// Same math as CalcMatrixFromRotationScale + CalcCovariance3D (M = R*S with row i of M =
// row i of R scaled componentwise, Sigma = M*M^T), written with float3 dot products only.
// The compute-stage HLSL->GLSL translation of 3x3 matrix constructs (casts, muls,
// transpose) is far less exercised on OpenGL ES than the vertex-stage one, so the splat
// projection path avoids 3x3 types entirely.
void CalcCovariance3DFromRotScale(float4 rot, float3 scale, out float3 sigma0, out float3 sigma1)
{
    float x = rot.x, y = rot.y, z = rot.z, w = rot.w;
    float3 m0 = float3(1-2*(y*y + z*z),   2*(x*y - w*z),   2*(x*z + w*y)) * scale;
    float3 m1 = float3(  2*(x*y + w*z), 1-2*(x*x + z*z),   2*(y*z - w*x)) * scale;
    float3 m2 = float3(  2*(x*z - w*y),   2*(y*z + w*x), 1-2*(x*x + y*y)) * scale;
    sigma0 = float3(dot(m0, m0), dot(m0, m1), dot(m0, m2)); // m00, m01, m02
    sigma1 = float3(dot(m1, m1), dot(m1, m2), dot(m2, m2)); // m11, m12, m22
}

// from "EWA Splatting" (Zwicker et al 2002) eq. 31
float3 CalcCovariance2D(float3 worldPos, float3 cov3d0, float3 cov3d1, float4x4 matrixV, float4x4 matrixP, float4 screenParams)
{
    float4x4 viewMatrix = matrixV;
    float3 viewPos = mul(viewMatrix, float4(worldPos, 1)).xyz;

    // this is needed in order for splats that are visible in view but clipped "quite a lot" to work
    float aspect = matrixP._m00 / matrixP._m11;
    float tanFovX = rcp(matrixP._m00);
    float tanFovY = rcp(matrixP._m11 * aspect);
    float limX = 1.3 * tanFovX;
    float limY = 1.3 * tanFovY;
    viewPos.x = clamp(viewPos.x / viewPos.z, -limX, limX) * viewPos.z;
    viewPos.y = clamp(viewPos.y / viewPos.z, -limY, limY) * viewPos.z;

    float focal = screenParams.x * matrixP._m00 / 2;

    // cov2d = T * V * T^T with T = J * W, written entirely in float3 dot products — no
    // (float3x3) cast, no 3x3 mul, no transpose() (see CalcCovariance3DFromRotScale).
    // W rows = upper-left 3x3 of the view matrix.
    float3 W0 = float3(viewMatrix._m00, viewMatrix._m01, viewMatrix._m02);
    float3 W1 = float3(viewMatrix._m10, viewMatrix._m11, viewMatrix._m12);
    float3 W2 = float3(viewMatrix._m20, viewMatrix._m21, viewMatrix._m22);
    // J = [f/z, 0, -f*x/z^2; 0, f/z, -f*y/z^2; 0,0,0] -> rows of T = J*W (row 2 drops out)
    float invZ = rcp(viewPos.z);
    float3 T0 = (focal * invZ) * W0 - (focal * viewPos.x * invZ * invZ) * W2;
    float3 T1 = (focal * invZ) * W1 - (focal * viewPos.y * invZ * invZ) * W2;
    // V symmetric 3x3 from cov3d packing
    float3 V0 = float3(cov3d0.x, cov3d0.y, cov3d0.z);
    float3 V1 = float3(cov3d0.y, cov3d1.x, cov3d1.y);
    float3 V2 = float3(cov3d0.z, cov3d1.y, cov3d1.z);
    float3 VT0 = float3(dot(V0, T0), dot(V1, T0), dot(V2, T0)); // V * T0 (V symmetric)
    float3 VT1 = float3(dot(V0, T1), dot(V1, T1), dot(V2, T1)); // V * T1
    float c00 = dot(T0, VT0);
    float c01 = dot(T0, VT1);
    float c11 = dot(T1, VT1);

    // Low pass filter to make each splat at least 1px size.
    c00 += 0.3;
    c11 += 0.3;
    return float3(c00, c01, c11);
}

void DecomposeCovariance(float3 cov2d, out float2 v1, out float2 v2)
{
    #if 0 // does not quite give the correct results?

    // https://jsfiddle.net/mattrossman/ehxmtgw6/
    // References:
    // - https://www.youtube.com/watch?v=e50Bj7jn9IQ
    // - https://en.wikipedia.org/wiki/Eigenvalue_algorithm#2%C3%972_matrices
    // - https://people.math.harvard.edu/~knill/teaching/math21b2004/exhibits/2dmatrices/index.html
    float a = cov2d.x;
    float b = cov2d.y;
    float d = cov2d.z;
    float det = a * d - b * b; // matrix is symmetric, so "c" is same as "b"
    float trace = a + d;

    float mean = 0.5 * trace;
    float dist = sqrt(mean * mean - det);

    float lambda1 = mean + dist; // 1st eigenvalue
    float lambda2 = mean - dist; // 2nd eigenvalue

    if (b == 0) {
        // https://twitter.com/the_ross_man/status/1706342719776551360
        if (a > d) v1 = float2(1, 0);
        else v1 = float2(0, 1);
    } else
        v1 = normalize(float2(b, d - lambda2));

    v1.y = -v1.y;
    // The 2nd eigenvector is just a 90 degree rotation of the first since Gaussian axes are orthogonal
    v2 = float2(v1.y, -v1.x);

    // scaling components
    v1 *= sqrt(lambda1);
    v2 *= sqrt(lambda2);

    float radius = 1.5;
    v1 *= radius;
    v2 *= radius;

    #else

    // same as in antimatter15/splat
    float diag1 = cov2d.x, diag2 = cov2d.z, offDiag = cov2d.y;
    float mid = 0.5f * (diag1 + diag2);
    float radius = length(float2((diag1 - diag2) / 2.0, offDiag));
    float lambda1 = mid + radius;
    float lambda2 = max(mid - radius, 0.1);
    // For isotropic splats this vector is ~zero and normalize() would produce NaN.
    // Metal/Vulkan silently discard NaN-position primitives so it goes unnoticed there,
    // but NaN rasterization is undefined on OpenGL ES (garbage needles). Guard it.
    float2 ev = float2(offDiag, lambda1 - diag1);
    float evLen2 = dot(ev, ev);
    float2 diagVec = evLen2 > 1e-12 ? ev * rsqrt(evLen2) : float2(1, 0);
    diagVec.y = -diagVec.y;
    float maxSize = 4096.0;
    v1 = min(sqrt(2.0 * lambda1), maxSize) * diagVec;
    v2 = min(sqrt(2.0 * lambda2), maxSize) * float2(diagVec.y, -diagVec.x);

    #endif
}

#endif // GAUSSIAN_SPLAT_MATH_HLSL
