﻿using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Globalization.NumberFormatting;

namespace Uno.UI.Tests.Windows_Globalization
{
	[TestClass]
	public class Given_SignificantDigitsNumberRounder
	{
		[DataTestMethod]
		[DataRow(123.456, (uint)8, 123.456)]
		[DataRow(123.456, (uint)6, 123.456)]
		[DataRow(123.456, (uint)4, 123.5)]
		[DataRow(123.456, (uint)2, 120)]
		[DataRow(123.456, (uint)1, 100)]
		[DataRow(123, (uint)5, 123)]
		public void When_UsingVariousSignificantDigits(double value, uint significantDigits, double expected)
		{
			var sut = new SignificantDigitsNumberRounder();
			sut.SignificantDigits = significantDigits;

			var rounded = sut.RoundDouble(value);
			Assert.AreEqual(expected, rounded);
		}

		[DataTestMethod]
		[DataRow(1.25, 1.3)]
		[DataRow(1.27, 1.3)]
		[DataRow(1.23, 1.3)]
		[DataRow(-1.25, -1.3)]
		[DataRow(-1.27, -1.3)]
		[DataRow(-1.23, -1.3)]
		public void When_RoundingAlgorithm_Is_RoundAwayFromZero(double value, double expected)
		{
			When_UsingARoundingAlgorithmCore(value, RoundingAlgorithm.RoundAwayFromZero, expected);
		}

		[DataTestMethod]
		[DataRow(1.25, 1.3)]
		[DataRow(1.27, 1.3)]
		[DataRow(1.23, 1.2)]
		[DataRow(-1.25, -1.3)]
		[DataRow(-1.27, -1.3)]
		[DataRow(-1.23, -1.2)]
		public void When_RoundingAlgorithm_Is_RoundHalfAwayFromZero(double value, double expected)
		{
			When_UsingARoundingAlgorithmCore(value, RoundingAlgorithm.RoundHalfAwayFromZero, expected);
		}

		[DataTestMethod]
		[DataRow(1.25, 1.3)]
		[DataRow(1.27, 1.3)]
		[DataRow(1.23, 1.3)]
		[DataRow(-1.25, -1.2)]
		[DataRow(-1.27, -1.2)]
		[DataRow(-1.23, -1.2)]
		public void When_RoundingAlgorithm_Is_RoundUp(double value, double expected)
		{
			When_UsingARoundingAlgorithmCore(value, RoundingAlgorithm.RoundUp, expected);
		}

		[DataTestMethod]
		[DataRow(1.25, 1.3)]
		[DataRow(1.27, 1.3)]
		[DataRow(1.23, 1.2)]
		[DataRow(-1.25, -1.2)]
		[DataRow(-1.27, -1.3)]
		[DataRow(-1.23, -1.2)]
		public void When_RoundingAlgorithm_Is_RoundHalfUp(double value, double expected)
		{
			When_UsingARoundingAlgorithmCore(value, RoundingAlgorithm.RoundHalfUp, expected);
		}

		[DataTestMethod]
		[DataRow(1.25, 1.2)]
		[DataRow(1.27, 1.2)]
		[DataRow(1.23, 1.2)]
		[DataRow(-1.25, -1.3)]
		[DataRow(-1.27, -1.3)]
		[DataRow(-1.23, -1.3)]
		public void When_RoundingAlgorithm_Is_RoundDown(double value, double expected)
		{
			When_UsingARoundingAlgorithmCore(value, RoundingAlgorithm.RoundDown, expected);
		}

		[DataTestMethod]
		[DataRow(1.25, 1.2)]
		[DataRow(1.27, 1.3)]
		[DataRow(1.23, 1.2)]
		[DataRow(-1.25, -1.3)]
		[DataRow(-1.27, -1.3)]
		[DataRow(-1.23, -1.2)]
		public void When_RoundingAlgorithm_Is_RoundHalfDown(double value, double expected)
		{
			When_UsingARoundingAlgorithmCore(value, RoundingAlgorithm.RoundHalfDown, expected);
		}

		[DataTestMethod]
		[DataRow(1.25, 1.2)]
		[DataRow(1.27, 1.2)]
		[DataRow(1.23, 1.2)]
		[DataRow(-1.25, -1.2)]
		[DataRow(-1.27, -1.2)]
		[DataRow(-1.23, -1.2)]
		public void When_RoundingAlgorithm_Is_RoundTowardsZero(double value, double expected)
		{
			When_UsingARoundingAlgorithmCore(value, RoundingAlgorithm.RoundTowardsZero, expected);
		}

		[DataTestMethod]
		[DataRow(1.25, 1.2)]
		[DataRow(1.27, 1.3)]
		[DataRow(1.23, 1.2)]
		[DataRow(-1.25, -1.2)]
		[DataRow(-1.27, -1.3)]
		[DataRow(-1.23, -1.2)]
		public void When_RoundingAlgorithm_Is_RoundHalfTowardsZero(double value, double expected)
		{
			When_UsingARoundingAlgorithmCore(value, RoundingAlgorithm.RoundHalfTowardsZero, expected);
		}

		[DataTestMethod]
		[DataRow(1.25, 1.2)]
		[DataRow(1.27, 1.3)]
		[DataRow(1.23, 1.2)]
		[DataRow(-1.25, -1.2)]
		[DataRow(-1.27, -1.3)]
		[DataRow(-1.23, -1.2)]
		[DataRow(1.35, 1.4)]
		[DataRow(1.37, 1.4)]
		[DataRow(1.33, 1.3)]
		[DataRow(-1.35, -1.4)]
		[DataRow(-1.37, -1.4)]
		[DataRow(-1.33, -1.3)]
		public void When_RoundingAlgorithm_Is_RoundHalfToEven(double value, double expected)
		{
			When_UsingARoundingAlgorithmCore(value, RoundingAlgorithm.RoundHalfToEven, expected);
		}

		[DataTestMethod]
		[DataRow(1.25, 1.3)]
		[DataRow(1.27, 1.3)]
		[DataRow(1.23, 1.2)]
		[DataRow(-1.25, -1.3)]
		[DataRow(-1.27, -1.3)]
		[DataRow(-1.23, -1.2)]
		[DataRow(1.35, 1.3)]
		[DataRow(1.37, 1.4)]
		[DataRow(1.33, 1.3)]
		[DataRow(-1.35, -1.3)]
		[DataRow(-1.37, -1.4)]
		[DataRow(-1.33, -1.3)]
		public void When_RoundingAlgorithm_Is_RoundHalfToOdd(double value, double expected)
		{
			When_UsingARoundingAlgorithmCore(value, RoundingAlgorithm.RoundHalfToOdd, expected);
		}

		[DataTestMethod]
		[DataRow(double.NaN, double.NaN)]
		[DataRow(double.NegativeInfinity, double.NaN)]
		[DataRow(double.PositiveInfinity, double.NaN)]
		public void When_Value_Is_Special(double value, double expected)
		{
			When_UsingARoundingAlgorithmCore(value, RoundingAlgorithm.RoundHalfUp, expected);
		}

		private void When_UsingARoundingAlgorithmCore(double value, RoundingAlgorithm roundingAlgorithm, double expected)
		{
			var sut = new SignificantDigitsNumberRounder();
			sut.SignificantDigits = 2;
			sut.RoundingAlgorithm = roundingAlgorithm;

			var rounded = sut.RoundDouble(value);
			Assert.AreEqual(expected, rounded);
		}


		[TestMethod]
		public void When_RoundingAlgorithm_Is_None_Then_Should_Throw()
		{
			var sut = new SignificantDigitsNumberRounder();
			Assert.ThrowsException<ArgumentException>(() => sut.RoundingAlgorithm = RoundingAlgorithm.None);
		}

		[TestMethod]
		public void When_SignificantDigits_Is_Zero_Then_Should_Throw()
		{
			var sut = new SignificantDigitsNumberRounder();
			Assert.ThrowsException<ArgumentException>(() => sut.SignificantDigits = 0);
		}
	}
}
