using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GlassM
{
	// Diag: standalone debug-output module (game console, diag.log, clipboard, B key).
	// Strip from release builds: dotnet build -c Release (defines PUBLIC_BUILD -> all no-ops).
	internal static class Diag
	{
#if !PUBLIC_BUILD
		private static readonly List<string> buffer = new List<string>();
		private static readonly string logPath = Path.Combine("BepInEx", "plugins", "GlassM-diag.log");
		private static readonly MethodInfo m_logToConsole = typeof(ConsoleScript).GetMethod("LogToConsole", BindingFlags.Instance | BindingFlags.NonPublic);

		public static void Log(string line)
		{
			Plugin.Log.LogInfo(line);
			LogToGameConsole(line);
			buffer.Add(line);
			while (buffer.Count > 300)
			{
				buffer.RemoveAt(0);
			}
			try
			{
				File.AppendAllText(logPath, line + Environment.NewLine);
			}
			catch
			{
			}
		}

		public static void LogToGameConsole(string text)
		{
			try
			{
				ConsoleScript instance = ConsoleScript.instance;
				if ((Object)(object)instance != (Object)null && m_logToConsole != null)
				{
					m_logToConsole.Invoke(instance, new object[1] { text });
				}
			}
			catch
			{
			}
		}

		public static string BuildText()
		{
			if (buffer.Count == 0)
			{
				try
				{
					if (File.Exists(logPath))
					{
						string[] lines = File.ReadAllLines(logPath);
						int take = Math.Min(50, lines.Length);
						return string.Join(Environment.NewLine, lines, lines.Length - take, take);
					}
				}
				catch
				{
				}
				return "(no CSDIAG lines yet)";
			}
			return string.Join(Environment.NewLine, buffer);
		}

		public static int BufferCount => buffer.Count;

		public static void HandleInput()
		{
			if (Input.GetKeyDown(KeyCode.B))
			{
				try
				{
					ConsoleScript cs = ConsoleScript.instance;
					if ((Object)(object)cs != (Object)null && cs.active)
					{
						return;
					}
				}
				catch
				{
				}
				string text = BuildText();
				try
				{
					GUIUtility.systemCopyBuffer = text;
				}
				catch
				{
				}
				LogToGameConsole("CSDIAG copied to clipboard: " + BufferCount + " lines, " + text.Length + " chars");
			}
		}
#else
		public static void Log(string line)
		{
		}

		public static void LogToGameConsole(string text)
		{
		}

		public static string BuildText()
		{
			return "(diag disabled)";
		}

		public static int BufferCount => 0;

		public static void HandleInput()
		{
		}
#endif
	}
}