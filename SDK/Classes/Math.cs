using System;

namespace HomefrontSDK;

// Hand-written halves of generated structs. Unreal units: 1 unit is about 2 cm; rotations are 65536 per turn.

public partial struct FVector
{
    public FVector(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static FVector Zero => default;

    public static FVector operator +(FVector a, FVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static FVector operator -(FVector a, FVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static FVector operator -(FVector a) => new(-a.X, -a.Y, -a.Z);
    public static FVector operator *(FVector a, float s) => new(a.X * s, a.Y * s, a.Z * s);
    public static FVector operator *(float s, FVector a) => a * s;
    public static FVector operator /(FVector a, float s) => new(a.X / s, a.Y / s, a.Z / s);

    public readonly float Size => MathF.Sqrt(X * X + Y * Y + Z * Z);
    public readonly float SizeSquared => X * X + Y * Y + Z * Z;
    public readonly float Size2D => MathF.Sqrt(X * X + Y * Y);

    /// <summary>The unit vector in the same direction, or zero.</summary>
    public readonly FVector Normal
    {
        get
        {
            float s = Size;
            return s > 1e-6f ? this / s : default;
        }
    }

    public static float Dot(FVector a, FVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static FVector Cross(FVector a, FVector b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static float Dist(FVector a, FVector b) => (a - b).Size;
    public static float Dist2D(FVector a, FVector b) => (a - b).Size2D;

    /// <summary>The rotation pointing along this vector (no roll).</summary>
    public readonly FRotator Rotation() => new(
        (int)(MathF.Atan2(Z, Size2D) * 32768f / MathF.PI),
        (int)(MathF.Atan2(Y, X) * 32768f / MathF.PI),
        0);

    public override readonly string ToString() => $"({X:0} {Y:0} {Z:0})";
}

public partial struct FRotator
{
    public FRotator(int pitch, int yaw, int roll)
    {
        Pitch = pitch;
        Yaw = yaw;
        Roll = roll;
    }

    /// <summary>The unit vector this rotation points along.</summary>
    public readonly FVector Vector()
    {
        float pitch = Pitch * MathF.PI / 32768f, yaw = Yaw * MathF.PI / 32768f;
        return new(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch));
    }

    public override readonly string ToString() => $"(pitch {Pitch} yaw {Yaw} roll {Roll})";
}
