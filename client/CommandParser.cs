using System.Collections.Generic;
using System.Text;

public static class CommandParser
{
	public static List<string> Tokenize(string input)
	{
		List<string> tokens = new List<string>();
		if (string.IsNullOrEmpty(input)) return tokens;

		StringBuilder currentToken = new StringBuilder();
		bool inQuotes = false;

		for (int i = 0; i < input.Length; i++)
		{
			char c = input[i];

			if (c == '"')
			{
				inQuotes = !inQuotes;
			}
			else if (c == ' ' && !inQuotes)
			{
				if (currentToken.Length > 0)
				{
					tokens.Add(currentToken.ToString());
					currentToken.Clear();
				}
			}
			else
			{
				currentToken.Append(c);
			}
		}

		if (currentToken.Length > 0)
		{
			tokens.Add(currentToken.ToString());
		}

		return tokens;
	}

	public static string GetActivePartialToken(string input)
	{
		if (string.IsNullOrEmpty(input)) return "";

		if (input.EndsWith(" ") && !input.Contains("\""))
		{
			return "";
		}

		List<string> tokens = Tokenize(input);
		if (tokens.Count > 0)
		{
			return tokens[tokens.Count - 1];
		}

		return "";
	}
}
