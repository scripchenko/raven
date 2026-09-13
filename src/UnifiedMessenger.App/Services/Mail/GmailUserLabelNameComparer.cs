using System.Globalization;

namespace UnifiedMessenger.App.Services.Mail;

internal sealed class GmailUserLabelNameComparer : IComparer<string>
{
    public static GmailUserLabelNameComparer Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        int leftIndex = 0;
        int rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            if (char.IsDigit(left[leftIndex]) && char.IsDigit(right[rightIndex]))
            {
                int numericComparison = CompareNumericRun(left, ref leftIndex, right, ref rightIndex);
                if (numericComparison != 0)
                {
                    return numericComparison;
                }

                continue;
            }

            int leftStart = leftIndex;
            int rightStart = rightIndex;
            while (leftIndex < left.Length && !char.IsDigit(left[leftIndex]))
            {
                leftIndex++;
            }

            while (rightIndex < right.Length && !char.IsDigit(right[rightIndex]))
            {
                rightIndex++;
            }

            int textComparison = CultureInfo.InvariantCulture.CompareInfo.Compare(
                left,
                leftStart,
                leftIndex - leftStart,
                right,
                rightStart,
                rightIndex - rightStart,
                CompareOptions.IgnoreCase);
            if (textComparison != 0)
            {
                return textComparison;
            }
        }

        return (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
    }

    private static int CompareNumericRun(
        string left,
        ref int leftIndex,
        string right,
        ref int rightIndex)
    {
        int leftRunStart = leftIndex;
        int rightRunStart = rightIndex;
        while (leftIndex < left.Length && char.IsDigit(left[leftIndex]))
        {
            leftIndex++;
        }

        while (rightIndex < right.Length && char.IsDigit(right[rightIndex]))
        {
            rightIndex++;
        }

        int leftSignificant = leftRunStart;
        int rightSignificant = rightRunStart;
        while (leftSignificant < leftIndex && left[leftSignificant] == '0')
        {
            leftSignificant++;
        }

        while (rightSignificant < rightIndex && right[rightSignificant] == '0')
        {
            rightSignificant++;
        }

        int digitCountComparison = (leftIndex - leftSignificant).CompareTo(rightIndex - rightSignificant);
        if (digitCountComparison != 0)
        {
            return digitCountComparison;
        }

        int valueComparison = string.CompareOrdinal(
            left,
            leftSignificant,
            right,
            rightSignificant,
            leftIndex - leftSignificant);
        if (valueComparison != 0)
        {
            return valueComparison;
        }

        return (leftIndex - leftRunStart).CompareTo(rightIndex - rightRunStart);
    }
}
