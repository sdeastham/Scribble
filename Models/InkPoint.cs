namespace Scribble.Models;

/// <summary>
/// A single stylus sample in PDF slide coordinates (top-left origin, in PDF points).
/// </summary>
public readonly record struct InkPoint(double X, double Y, float Pressure);
