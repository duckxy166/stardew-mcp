using System;

// Stand-ins for the two MonoGame structs the linked mod files use (Point and Vector2), so the tests
// need neither the game nor the MonoGame package. Only the members the linked files call are here,
// with the same names and meaning as MonoGame's: public X/Y fields, Point.ToVector2(), == on Point.
namespace Microsoft.Xna.Framework
{
    public struct Point : IEquatable<Point>
    {
        public int X;
        public int Y;

        public Point(int x, int y)
        {
            X = x;
            Y = y;
        }

        public Vector2 ToVector2() => new Vector2(X, Y);

        public bool Equals(Point other) => X == other.X && Y == other.Y;

        public override bool Equals(object? obj) => obj is Point other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y);

        public static bool operator ==(Point a, Point b) => a.Equals(b);

        public static bool operator !=(Point a, Point b) => !a.Equals(b);

        public override string ToString() => $"{{X:{X} Y:{Y}}}";
    }

    public struct Vector2 : IEquatable<Vector2>
    {
        public float X;
        public float Y;

        public Vector2(float x, float y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(Vector2 other) => X == other.X && Y == other.Y;

        public override bool Equals(object? obj) => obj is Vector2 other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y);

        public static bool operator ==(Vector2 a, Vector2 b) => a.Equals(b);

        public static bool operator !=(Vector2 a, Vector2 b) => !a.Equals(b);

        public override string ToString() => $"{{X:{X} Y:{Y}}}";
    }
}
