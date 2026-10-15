using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Ventrail.Converters;

/// <summary>Visible when the bound enum value equals ConverterParameter (used to show the current page).</summary>
public class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Equals(value, parameter) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the bound enum equals ConverterParameter; checking a radio button sets the enum.</summary>
public class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Equals(value, parameter);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;
}

/// <summary>Collapsed when true, Visible when false (for "nothing here yet" messages).</summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
