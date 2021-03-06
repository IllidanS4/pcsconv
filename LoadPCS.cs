﻿/*
 * Created by SharpDevelop.
 * User: Illidan
 * Date: 7.12.2013
 * Time: 15:13
 * 
 * To change this template use Tools | Options | Coding | Edit Standard Headers.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace speakerconv
{
	/// <summary>
	/// Description of LoadPCS.
	/// </summary>
	public class LoadPCS : InputProcessor
	{
		public LoadPCS()
		{
			
		}
		
		public override IList<OutputFile> ProcessStream(Stream input, ConvertOptions options)
		{
			List<Command> commands = new List<Command>();
			int linenum = 0;
			using(StreamReader reader = new StreamReader(input))
			{
				string line;
				while((line = reader.ReadLine()) != null)
				{
					linenum += 1;
					if(line.StartsWith("$"))
					{
						var match = Regex.Match(line, @"^\$(?<port>.+?)(:(?<state>.+?))?=(?<value>.+?)@(?<time>.+?)$");
						if(match.Success)
						{
							int port = Convert.ToInt32(match.Groups["port"].Value, 16);
							string sstate = match.Groups["state"].Value;
							int state = String.IsNullOrEmpty(sstate)?-1:Convert.ToInt32(sstate, 16);
							byte value = Convert.ToByte(match.Groups["value"].Value, 16);
							int time = Convert.ToInt32(match.Groups["time"].Value);
							switch(port)
							{
								case 0x42:
									if(state == 3)
									{
										commands.Add(new FrequencyByte1Command(value, time));
									}else if(state == 0)
									{
										commands.Add(new FrequencyByte2Command(value, time));
									}else{
										Program.Warning("{0}: Unknown port state ({1:x}).", linenum, state);
									}
									break;
								case 0x43:
									commands.Add(new FrequencyModeCommand((value & 0x0E) >> 1, time));
									break;
								case 0x61:
									if(value != state)
									{
										int diff = value ^ state;
										if(diff == 3)
										{
											commands.Add(new EnableCommand((value & 3) == 3, time));
										}else if(diff == 51)
										{
											commands.Add(new EnableCommand(false, time));
										}
									}
									break;
								default:
									Program.Warning("{0}: Undefined port {1:x}.", linenum, port);
									break;
							}
						}else{
							Program.Warning("{0}: Undefined line format.", linenum);
						}
					}
				}
			}
			
			return ProcessRPC(ProcessPCS(commands, options), options);
		}
		
		public static List<RPCCommand> ProcessPCS(IList<Command> commands, ConvertOptions options)
		{
			List<RPCCommand> rpc = new List<RPCCommand>();
			
			bool enabled = false;
			int starttime = -1;
			int lasttime = 0;
			FrequencyMode freqmode = 0;
			
			int? setcountdown = null;
			
			var e = commands.GetEnumerator();
			while(e.MoveNext())
			{
				if(starttime == -1)
				{
					starttime = e.Current.Time;
				}else{
					int timedelta  = e.Current.Time-lasttime;
					if(timedelta > 0)
					{
						rpc.Add(RPCCommand.Delay(timedelta));
					}
				}
				lasttime = e.Current.Time;
				if(e.Current is EnableCommand)
				{
					enabled = ((EnableCommand)e.Current).Enable;
					if(enabled == false)
					{
						rpc.Add(RPCCommand.ClearCountdown());
					}else if(setcountdown != null)
					{
						rpc.Add(new RPCCommand(RPCCommandType.SetCountdown, setcountdown.Value));
						setcountdown = null;
					}
				}else if(e.Current is FrequencyModeCommand)
				{
					freqmode = ((FrequencyModeCommand)e.Current).Mode;
				}else if(e.Current is FrequencyByte1Command && freqmode != 0)
				{
					if(freqmode == FrequencyMode.Countdown || freqmode == FrequencyMode.FrequencyDivider)
					{
						int countdown = ((FrequencyByte1Command)e.Current).Value;
						e.MoveNext();
						try{
							countdown |= ((FrequencyByte2Command)e.Current).Value << 8;
							//if(countdown != 0)
								// /*if(enabled) */rpc.Add(new RPCCommand(RPCCommandType.SetCountdown, countdown));
							if(enabled)
							{
								rpc.Add(new RPCCommand(RPCCommandType.SetCountdown, countdown));
							}else{
								setcountdown = countdown;
							}
						}catch(InvalidCastException)
						{
							Program.Error("Missing countdown pair for $42.");
						}
					}else{
						Program.Warning("Unknown value {0:x} for port $42.", freqmode);
					}
				}else{
					Program.Warning("Unknown port ${0:x}.", e.Current.Port);
				}
			}
			
			return rpc;
		}
		
		public static IList<OutputFile> ProcessRPC(List<RPCCommand> rpc, ConvertOptions options)
		{
			bool informed = false;
			
			for(int i = 0; i < rpc.Count; i++)
			{
				var cmd = rpc[i];
				if(cmd.Type == RPCCommandType.SetCountdown)
				{
					if(cmd.Data <= 36 || cmd.Data > 32248)
					{
						if(options.PCS_Sanitize)
						{
							rpc.RemoveAt(i);
							i -= 1;
						}else if(!informed)
						{
							informed = true;
							Console.WriteLine("Input contains frequencies ({0}) outside the standard range, use --sanitize to remove them.", 1193180/cmd.Data);
						}
					}
				}
				if(cmd.Type == RPCCommandType.Delay && cmd.Data == 0)
				{
					rpc.RemoveAt(i);
					i -= 1;
				}else if(cmd.Type == RPCCommandType.Delay)
				{
					if(i+1 < rpc.Count && rpc[i+1].Type == RPCCommandType.Delay)
					{
						int delay = cmd.Data + rpc[i+1].Data;
						rpc[i] = new RPCCommand(RPCCommandType.Delay, delay);
						rpc.RemoveAt(i+1);
						i -= 1;
					}
				}else{
					if(i+1 < rpc.Count && rpc[i+1].Type == cmd.Type && rpc[i+1].Data == cmd.Data)
					{
						rpc.RemoveAt(i);
						i -= 1;
					}
				}
			}
			
			if(options.PCS_Trim)
			{
				for(int i = 0; i < rpc.Count; i++)
				{
					var cmd = rpc[i];
					if(cmd.Type == RPCCommandType.SetCountdown)
					{
						break;
					}else{
						rpc.RemoveAt(i);
						i -= 1;
					}
				}
				for(int i = rpc.Count-1; i >= 0; i--)
				{
					var cmd = rpc[i];
					if(cmd.Type == RPCCommandType.SetCountdown)
					{
						break;
					}else{
						rpc.RemoveAt(i);
					}
				}
			}
			
			IList<OutputFile> files;
			if(options.PCS_Split)
			{
				bool playing = false;
				files = rpc.Split(
					c => c.Type == RPCCommandType.SetCountdown ? (playing = true) && false : c.Type == RPCCommandType.ClearCountdown ? (playing = false) && false : c.Type == RPCCommandType.Delay ? c.Data >= options.PCS_SplitDelay*1000 && !playing : false
				).Select((d,i) => new OutputFile(Path.ChangeExtension(Path.ChangeExtension(options.OutputPath, null)+i.ToString("000"), options.Extension), d)).ToList();
			}else{
				files = new[]{new OutputFile(options.OutputPath, rpc)};
			}
			
			if(options.PCS_Crop)
			{
				foreach(var file in files)
				{
					var data = file.Data;
					int start = 0;
					for(int i = 0; i < data.Count; i++)
					{
						var cmd = data[i];
						if(cmd.Type == RPCCommandType.SetCountdown)
						{
							start = i; break;
						}
					}
					for(int i = start+1; i < data.Count; i++)
					{
						var cmd = data[i];
						if(cmd.Type == RPCCommandType.Delay) continue;
						for(int j = i; j < data.Count; j++)
						{
							var left = data[start+j-i];
							var right = data[j];
							if(left.Type != right.Type) break;
							if(left.Type == RPCCommandType.SetCountdown ? left.Data != right.Data : false) break;
							if(left.Type == RPCCommandType.Delay ? right.Data < left.Data-2 || right.Data > left.Data+2 : false) break;
							if(j-i > options.PCS_CropSimilarity || (j == data.Count-3 && j-i > 5))
							{
								data.RemoveRange(i, data.Count-i);
							}
						}
					}
				}
			}
			
			if(options.PCS_TrimLength)
			{
				foreach(var file in files)
				{
					var data = new List<RPCCommand>(file.Data);
					int time = 0;
					for(int i = 0; i < data.Count; i++)
					{
						var cmd = data[i];
						if(cmd.Type == RPCCommandType.Delay)
						{
							time += cmd.Data;
							if(time > options.PCS_NewLength)
							{
								data[i] = new RPCCommand(cmd.Type, cmd.Channel, cmd.DelayValue-(time-options.PCS_NewLength));
								data.RemoveRange(i+1, data.Count-(i+1));
								data.Add(RPCCommand.ClearCountdown());
							}
						}
					}
				}
			}
			
			if(options.PCS_Repeat)
			{
				foreach(var file in files)
				{
					var data = new List<RPCCommand>(file.Data);
					for(int i = 0; i < options.PCS_RepeatCount-1; i++)
					{
						file.Data.AddRange(data);
					}
				}
			}
			
			
			if(options.Optimize)
			{
				foreach(var file in files)
				{
					var data = file.Data;
					
					int? lastfreq = null;
					for(int i = 0; i < data.Count; i++)
					{
						var cmd = data[i];
						if(cmd.Type == RPCCommandType.SetCountdown)
						{
							if(lastfreq != null && Math.Abs(lastfreq.Value-cmd.Data) <= 1)
							{
								lastfreq = cmd.Data;
								data.RemoveAt(i);
								i -= 1;
							}else{
								lastfreq = cmd.Data;
							}
						}else if(cmd.Type == RPCCommandType.ClearCountdown)
						{
							lastfreq = null;
						}
					}
				}
			}
			
			if(options.OutputType == "dro")
			{
				//Separates pairs of SetCountdown/ClearCountdown by a Delay: 0
				//Required for DRO rendering
				
				foreach(var file in files)
				{
					var data = file.Data;
					
					bool isclick = false;
					for(int i = 0; i < data.Count; i++)
					{
						var cmd = data[i];
						if(cmd.Type == RPCCommandType.SetCountdown)
						{
							isclick = true;
						}else if(cmd.Type == RPCCommandType.Delay)
						{
							isclick = false;
						}else if(cmd.Type == RPCCommandType.ClearCountdown)
						{
							if(isclick)
							{
								data.Insert(i, RPCCommand.Delay(0));
								i += 1;
							}
							isclick = false;
						}
					}
				}
			}
			
			return files;
		}
	}
}
