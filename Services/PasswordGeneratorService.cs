using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Evania.Services;

/// <summary>
/// Options for <see cref="PasswordGeneratorService"/>. At least one character class must stay
/// enabled; <see cref="Length"/> is clamped into a sane range by the generator.
/// </summary>
public sealed class GeneratorOptions
{
    public int Length { get; set; } = 20;

    public bool Lowercase { get; set; } = true;

    public bool Uppercase { get; set; } = true;

    public bool Digits { get; set; } = true;

    public bool Symbols { get; set; } = true;

    /// <summary>Drops look-alike characters (il1Lo0O) so the result survives being read aloud.</summary>
    public bool AvoidAmbiguous { get; set; }
}

public static class PasswordGeneratorService
{
    public const int MinLength = 4;
    public const int MaxLength = 256;

    private const string LowercaseChars = "abcdefghijklmnopqrstuvwxyz";
    private const string UppercaseChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string DigitChars = "0123456789";
    private const string SymbolChars = "!@#$%^&*()-_=+[]{};:,.<>?/~";
    private const string AmbiguousChars = "il1Lo0O";

    public static string Generate(GeneratorOptions options)
    {
        List<string> pools = BuildPools(options);
        if (pools.Count == 0)
        {
            throw new ArgumentException("At least one character class must be selected.");
        }

        int length = Math.Clamp(options.Length, MinLength, MaxLength);

        // Guarantee one character from every selected class, then fill the rest from the combined
        // pool and shuffle, so the result satisfies "must contain a digit/symbol"-style policies
        // without the guarantee leaking position information.
        StringBuilder builder = new(length);
        foreach (string pool in pools)
        {
            builder.Append(pool[RandomNumberGenerator.GetInt32(pool.Length)]);
        }

        string combined = string.Concat(pools);
        while (builder.Length < length)
        {
            builder.Append(combined[RandomNumberGenerator.GetInt32(combined.Length)]);
        }

        char[] characters = builder.ToString().ToCharArray();
        Shuffle(characters);

        return new string(characters);
    }

    /// <summary>Entropy in bits for the given options - a rough strength estimate for the UI.</summary>
    public static int EstimateStrengthBits(GeneratorOptions options)
    {
        int poolSize = BuildPools(options).Sum(pool => pool.Length);
        if (poolSize <= 1)
        {
            return 0;
        }

        int length = Math.Clamp(options.Length, MinLength, MaxLength);

        return (int)(length * Math.Log2(poolSize));
    }

    private static List<string> BuildPools(GeneratorOptions options)
    {
        List<string> pools = new(4);

        AddPool(pools, LowercaseChars, options.Lowercase, options.AvoidAmbiguous);
        AddPool(pools, UppercaseChars, options.Uppercase, options.AvoidAmbiguous);
        AddPool(pools, DigitChars, options.Digits, options.AvoidAmbiguous);
        AddPool(pools, SymbolChars, options.Symbols, avoidAmbiguous: false);

        return pools;
    }

    private static void AddPool(List<string> pools, string characters, bool enabled, bool avoidAmbiguous)
    {
        if (!enabled)
        {
            return;
        }

        string pool = avoidAmbiguous
            ? new string(characters.Where(c => AmbiguousChars.IndexOf(c) < 0).ToArray())
            : characters;

        if (pool.Length > 0)
        {
            pools.Add(pool);
        }
    }

    private static void Shuffle(char[] characters)
    {
        for (int i = characters.Length - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (characters[i], characters[j]) = (characters[j], characters[i]);
        }
    }
}
