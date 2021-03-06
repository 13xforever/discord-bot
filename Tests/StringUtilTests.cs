﻿using System.Linq;
using CompatBot.EventHandlers;
using CompatBot.Utils;
using CompatBot.Utils.Extensions;
using DuoVia.FuzzyStrings;
using HomoglyphConverter;
using NUnit.Framework;

namespace Tests
{
    [TestFixture]
    public class StringUtilTests
    {
        [TestCase("🇭🇷", ExpectedResult = 2)]
        [TestCase("\u200d", ExpectedResult = 0)]
        [TestCase("㌀", ExpectedResult = 1)]
        [TestCase("a\u0304\u0308bc\u0327", ExpectedResult = 3)]
        [TestCase("Megamouse", ExpectedResult = 9)]
        public int VisibleLengthTest(string input) => input.GetVisibleLength();

        [Test]
        public void VisibleTrimTest()
        {
            Assert.That("abc".TrimVisible(100), Is.EqualTo("abc"));
            Assert.That("abc".TrimVisible(3), Is.EqualTo("abc"));
            Assert.That("abc".TrimVisible(2), Is.EqualTo("a…"));
        }

        [TestCase("cockatrice", "сockаtrice")]
        [TestCase("cockatrice", "çöćķåťřĩĉȅ")]
        public void DiacriticsDetectionTest(string strA, string strB)
        {
            Assert.That(strA.EqualsIgnoringDiacritics(strB), Is.True);
        }

        [TestCase("cockatrice", "cockatrice")]
        [TestCase("сосkаtriсе", "cockatrice")]
        [TestCase("cocкatrice", "cockatrice")]
        [TestCase("c‎ockatrice", "cockatrice")]
        [TestCase("соc͏katrice", "cockatrice")]
        [TestCase("çöćķåťřĩĉȅ", "cockatrice")]
        [TestCase("с⁪◌ck⁬åťřĩĉȅ", "cockatrice")]
        [TestCase("jò̵͗s̷̑͠ẻ̵͝p̸̆̂h̸͐̿", "joseph")]
        public void HomoglyphDetectionTest(string strA, string strB)
        {
            Assert.That(strA.StripInvisibleAndDiacritics().ToCanonicalForm(), Is.EqualTo(strB));
        }

        [TestCase("jò̵͗s̷̑͠ẻ̵͝p̸̆̂h̸͐̿", "joseph")]
        public void StripZalgoTest(string input, string expected)
        {
            var stripped = UsernameZalgoMonitor.StripZalgo(input, 0ul);
            Assert.That(stripped, Is.EqualTo(expected));
        }

        [Test]
        public void SubstringTest()
        {
            var contentId = "UP2611-NPUB31848_00-HDDBOOTPERSONA05";
            var productCode = "NPUB31848";
            Assert.That(contentId[7..16], Is.EqualTo(productCode));
        }

        [TestCase("a grey and white cat sitting in front of a window", ExpectedResult = "a grey and white kot sitting in front of a window")]
        public string FixKotTest(string input) => input.FixKot();

        [TestCase("minesweeeper", "minesweeper")]
        [TestCase("minesweeeeeeeeeeeeeeeeeeper", "minesweeper")]
        [TestCase("ee", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee")]
        [TestCase("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "ee")]
        public void DiceCoefficientRangeTest(string strA, string strB)
        {
            var coef = DiceCoefficient(strA, strB);
            Assert.That(coef, Is.GreaterThanOrEqualTo(0.0).And.LessThanOrEqualTo(1.0));
            Assert.That(DiceCoefficientExtensions.DiceCoefficient(strA, strB), Is.EqualTo(coef));
            Assert.That(DiceCoefficientOptimized.DiceCoefficient(strA, strB), Is.LessThanOrEqualTo(coef));

            var tmp = strA;
            strA = strB;
            strB = tmp;
            var coefB = DiceCoefficient(strA, strB);
            Assert.That(coefB, Is.EqualTo(coef));
            Assert.That(DiceCoefficientExtensions.DiceCoefficient(strA, strB), Is.EqualTo(coef));
            Assert.That(DiceCoefficientOptimized.DiceCoefficient(strA, strB), Is.LessThanOrEqualTo(coef));
        }

        public static double DiceCoefficient(string input, string comparedTo)
        {
            var ngrams = input.ToBiGrams();
            var compareToNgrams = comparedTo.ToBiGrams();
            return DiceCoefficient(ngrams, compareToNgrams);
        }

        public static double DiceCoefficient(string[] nGrams, string[] compareToNGrams)
        {
            var matches = nGrams.Intersect(compareToNGrams).Count();
            if (matches == 0)
                return 0.0d;

            double totalBigrams = nGrams.Length + compareToNGrams.Length;
            return (2 * matches) / totalBigrams;
        }
    }
}
