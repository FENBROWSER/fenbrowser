using System;

namespace FenBrowser.FenEngine.Layout.Contexts
{
    internal static class TextWhitespaceClassifier
    {
        internal static bool IsCollapsibleWhitespaceOnly(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return true;
            }

            foreach (char ch in text)
            {
                if (!IsCollapsibleWhitespace(ch))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsCollapsibleWhitespace(char ch)
        {
            return ch == ' ' ||
                   ch == '\t' ||
                   ch == '\n' ||
                   ch == '\r' ||
                   ch == '\f';
        }

        internal static bool IsCollapsibleWhitespaceChar(char ch) => IsCollapsibleWhitespace(ch);
    }
}
