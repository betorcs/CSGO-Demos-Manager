using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Core;
using Core.Models;
using Core.Models.protobuf;
using Core.Models.Serialization;
using Core.Models.Source;
using ICSharpCode.SharpZipLib.BZip2;
using Newtonsoft.Json;
using ProtoBuf;
using Services.Concrete.Analyzer;
using Services.Interfaces;
using Demo = Core.Models.Demo;
using Player = Core.Models.Player;

namespace Services.Concrete
{
	public class DemosService : IDemosService
	{
		public string DownloadFolderPath { get; set; }

		public long SelectedStatsAccountSteamId { get; set; }

		public bool ShowOnlyAccountDemos { get; set; } = false;

		public bool IgnoreLaterBan { get; set; }

		private readonly CacheService _cacheService = new CacheService();

		private readonly ISteamService _steamService;

		public DemosService(ISteamService steamService)
		{
			_steamService = steamService;
		}

		/// <summary>
		/// Check if there are banned players and update their banned flags
		/// </summary>
		/// <param name="demo"></param>
		/// <returns></returns>
		public async Task<Demo> AnalyzeBannedPlayersAsync(Demo demo)
		{
			List<string> ids = demo.Players.Select(playerExtended => playerExtended.SteamId.ToString()).ToList();
			IEnumerable<Suspect> suspects = await _steamService.GetBanStatusForUserList(ids);
			var enumerableSuspects = suspects as IList<Suspect> ?? suspects.ToList();
			if (enumerableSuspects.Any())
			{
				List<string> whitelistIds =  await _cacheService.GetPlayersWhitelist();
				// Update player's flag
				foreach (Suspect suspect in enumerableSuspects)
				{
					Player cheater = demo.Players.FirstOrDefault(p => p.SteamId.ToString() == suspect.SteamId);
					if (cheater != null && !whitelistIds.Contains(cheater.SteamId.ToString()))
					{
						if (IgnoreLaterBan && DateTime.Now.AddDays(-suspect.DaySinceLastBanCount) < demo.Date)
							continue;
						if (suspect.GameBanCount > 0)
						{
							demo.HasCheater = true;
							cheater.IsOverwatchBanned = true;
						}
						if (suspect.VacBanned)
						{
							demo.HasCheater = true;
							cheater.IsVacBanned = true;
						}
					}
				}
			}
			return demo;
		}

		public async Task<Demo> GetDemoHeaderAsync(string demoFilePath)
		{
			Demo demo = await Task.Run(() => DemoAnalyzer.ParseDemoHeader(demoFilePath));
			if (demo != null)
			{
				// Update the demo name and path because it may be renamed / moved
				demo.Name = Path.GetFileName(demoFilePath);
				demo.Path = demoFilePath;
			}

			return demo;
		}

		public async Task<Demo> GetDemoDataAsync(Demo demo)
		{
			// If demo is in cache we retrieve its data
			bool demosIsCached = _cacheService.HasDemoInCache(demo.Id);
			if (demosIsCached)
			{
				demo = await _cacheService.GetDemoDataFromCache(demo.Id);
				demo.Source = Source.Factory(demo.SourceName);
			}

			return demo;
		}

		public async Task<Demo> GetDemoDataByIdAsync(string demoId)
		{
			Demo demo = new Demo
			{
				Id = demoId
			};
			bool demosIsCached = _cacheService.HasDemoInCache(demoId);
			if (demosIsCached)
			{
				demo = await _cacheService.GetDemoDataFromCache(demoId);
				demo.Source = Source.Factory(demo.SourceName);
			}

			return demo;
		}

		public async Task<List<Demo>> GetDemosHeader(List<string> folders, List<Demo> currentDemos = null, int demoSize = 0)
		{
			// Demos list returned
			List<Demo> demos = new List<Demo>();
			// Contains all demos header data
			List<Demo> demoHeaderList = new List<Demo>();
			if (folders.Count > 0)
			{
				// retrieve basic demos data
				List<DemoBasicData> demoBasicDataList = await _cacheService.GetDemoBasicDataAsync();
				// contains possible DemoBasicData that will be keeped
				List<DemoBasicData> tempDemoBasicDataList = new List<DemoBasicData>();
				foreach (string folder in folders)
				{
					if (Directory.Exists(folder))
					{
						string[] files = Directory.GetFiles(folder, "*.dem");
						foreach (string file in files)
						{
							if (file.Contains("myassignedcase")) continue;
							Demo demo = await GetDemoHeaderAsync(file);
							if (demo != null)
							{
								// Skip if the demo is already in the current demos list
								if ((currentDemos != null && currentDemos.Contains(demo)) || demoHeaderList.Contains(demo))
									continue;
								demoHeaderList.Add(demo);
							}
						}
					}
				}

				// Process each demo header
				foreach (Demo demo in demoHeaderList)
				{
					// retrieve basic demo data
					DemoBasicData demoData = demoBasicDataList.FirstOrDefault(d => d.Id == demo.Id);
					if (demoData == null)
					{
						// if basic data are not found, add it to cache
						demoData = await _cacheService.AddDemoBasicDataAsync(demo);
					}
					// Skip if the player isn't in the demo
					if (ShowOnlyAccountDemos && SelectedStatsAccountSteamId != 0
					    && !demoData.SteamIdList.Contains(SelectedStatsAccountSteamId))
						continue;

					// store basic data to filter on date quickly
					tempDemoBasicDataList.Add(demoData);
				}

				tempDemoBasicDataList.Sort((d1, d2) => d2.Date.CompareTo(d1.Date));
				if (demoSize > 0) tempDemoBasicDataList = tempDemoBasicDataList.Take(demoSize).ToList();

				foreach (DemoBasicData basicData in tempDemoBasicDataList)
				{
					Demo demo;
					bool hasDemoInCache = _cacheService.HasDemoInCache(basicData.Id);
					if (hasDemoInCache)
					{
						demo = await GetDemoDataByIdAsync(basicData.Id);
					}
					else
					{
						demo = demoHeaderList.First(d => d.Id == basicData.Id);
					}
					if (currentDemos == null || !currentDemos.Contains(demo))
					{
						demos.Add(demo);
					}
				}
			}

			return demos;
		}

		public async Task<Demo> AnalyzeDemo(Demo demo, CancellationToken token)
		{
			if (!File.Exists(demo.Path))
			{
				// Demo may be moved to an other folder, just clear cache
				await _cacheService.RemoveDemo(demo.Id);
			}

			DemoAnalyzer analyzer = DemoAnalyzer.Factory(demo);

			demo = await analyzer.AnalyzeDemoAsync(token);

			return demo;
		}

		public async Task SaveComment(Demo demo, string comment)
		{
			demo.Comment = comment;
			await _cacheService.WriteDemoDataCache(demo);
		}

		public async Task SaveStatus(Demo demo, string status)
		{
			demo.Status = status;
			await _cacheService.WriteDemoDataCache(demo);
		}

		public async Task<ObservableCollection<Demo>>  SetSource(ObservableCollection<Demo> demos, string source)
		{
			foreach (Demo demo in demos.Where(demo => demo.Type != "POV"))
			{
				await SetSource(demo, source);
			}

			return demos;
		}

		public async Task<Demo> SetSource(Demo demo, string source)
		{
			if (demo.SourceName != source)
			{
				demo.SourceName = source;
				await _cacheService.WriteDemoDataCache(demo);
			}

			return demo;
		}

		public async Task<Demo> AnalyzePlayersPosition(Demo demo, CancellationToken token)
		{
			if (!File.Exists(demo.Path))
			{
				// Demo may be moved to an other folder, just clear cache
				await _cacheService.RemoveDemo(demo.Id);
			}

			DemoAnalyzer analyzer = DemoAnalyzer.Factory(demo);
			analyzer.AnalyzePlayersPosition = true;

			demo = await analyzer.AnalyzeDemoAsync(token);

			await _cacheService.WriteDemoDataCache(demo);

			return demo;
		}

		/// <summary>
		/// Return demos from JSON backup file
		/// </summary>
		/// <returns></returns>
		public async Task<List<Demo>> GetDemosFromBackup(string jsonFile)
		{
			List<Demo> demos = new List<Demo>();
			if (File.Exists(jsonFile))
			{
				string json = File.ReadAllText(jsonFile);
				demos = await Task.Factory.StartNew(() => JsonConvert.DeserializeObject<List<Demo>>(json, new DemoListBackupConverter()));
			}
			return demos;
		}

		public async Task<Rank> GetLastRankAccountStatsAsync(long steamId)
		{
			Rank lastRank = AppSettings.RankList[0];
			List<string> folders = await _cacheService.GetFoldersAsync();
			// Get only the demos header to have access to the demo's date
			List<Demo> demos = await GetDemosHeader(folders);
			if (demos.Any())
			{
				if (steamId == 0) steamId = SelectedStatsAccountSteamId;
				if (steamId == 0) return lastRank;
				// keep only demos from valve where the account played
				List<Demo> newDemoList = demos.Where(d => d.SourceName == "valve"
				&& d.Players.FirstOrDefault(p => p.SteamId == steamId) != null)
				.ToList();
				if (newDemoList.Any())
				{
					// Sort by date and keep the more recent
					newDemoList.Sort((d1, d2) => d1.Date.CompareTo(d2.Date));
					Demo lastDemo = newDemoList.Last();
					// Get the new rank
					Player player = lastDemo.Players.First(p => p.SteamId == steamId);
					lastRank = AppSettings.RankList.FirstOrDefault(r => r.Number == player.RankNumberNew);
					// Save it to the cache
					RankInfo rankInfo = new RankInfo
					{
						Number = player.RankNumberNew,
						SteamId = player.SteamId,
						LastDate = lastDemo.Date
					};
					await _cacheService.SaveLastRankInfoAsync(rankInfo);
				}
			}

			return lastRank;
		}

		public async Task<List<Demo>> GetDemosPlayer(string steamId)
		{
			List<Demo> result = new List<Demo>();

			List<Demo> demos = await _cacheService.GetDemoListAsync();

			if (demos.Any())
			{
				result = demos.Where(demo => demo.Players.FirstOrDefault(p => p.SteamId.ToString() == steamId) != null).ToList();
				for(int i = 0; i < result.Count; i++)
				{
					Demo demo = await GetDemoHeaderAsync(result[i].Path);
					if (demo != null) result[i] = demo;
				}
			}

			return result;
		}

		public async Task<bool> DeleteDemo(Demo demo)
		{
			if (!File.Exists(demo.Path)) return false;
			await _cacheService.RemoveDemo(demo.Id);
			string infoFilePath = demo.Path + ".info";
			if (File.Exists(infoFilePath))
				FileOperationApiWrapper.Send(infoFilePath, FileOperationApiWrapper.FileOperationFlags.FOF_SILENT);
			FileOperationApiWrapper.Send(demo.Path, FileOperationApiWrapper.FileOperationFlags.FOF_SILENT);

			return true;
		}

		public async Task<bool> DownloadDemo(string url, string demoName)
		{
			using (WebClient webClient = new WebClient())
			{
				try
				{
					string location = AppSettings.GetFolderCachePath() + Path.DirectorySeparatorChar + demoName + ".bz2";
					Uri uri = new Uri(url);
					await Task.Factory.StartNew(() => webClient.DownloadFile(uri, location));
					return true;
				}
				catch (Exception e)
				{
					Logger.Instance.Log(e);
					return false;
				}
			}
		}

		public async Task<bool> DecompressDemoArchive(string demoName)
		{
			string archivePath = AppSettings.GetFolderCachePath() + Path.DirectorySeparatorChar + demoName + ".bz2";
			string destination = DownloadFolderPath + Path.DirectorySeparatorChar + demoName + ".dem";
			if (!File.Exists(archivePath)) return false;
			await Task.Factory.StartNew(() => BZip2.Decompress(File.OpenRead(archivePath), File.Create(destination), true));
			File.Delete(archivePath);
			return true;
		}

		public async Task<Dictionary<string, string>> GetDemoListUrl()
		{
			Dictionary<string, string> demoUrlList = new Dictionary<string, string>();

			string filePath = AppSettings.GetMatchListDataFilePath();
			if (File.Exists(filePath))
			{
				using (FileStream file = File.OpenRead(filePath))
				{
					try
					{
						CMsgGCCStrike15_v2_MatchList matchList = Serializer.Deserialize<CMsgGCCStrike15_v2_MatchList>(file);
						foreach (CDataGCCStrike15_v2_MatchInfo matchInfo in matchList.matches)
						{
							// old definition
							if (matchInfo.roundstats_legacy != null)
							{
								CMsgGCCStrike15_v2_MatchmakingServerRoundStats roundStats = matchInfo.roundstats_legacy;
								await ProcessRoundStats(matchInfo, roundStats, demoUrlList);
							}
							else
							{
								// new definition
								List<CMsgGCCStrike15_v2_MatchmakingServerRoundStats> roundStatsList = matchInfo.roundstatsall;
								foreach (CMsgGCCStrike15_v2_MatchmakingServerRoundStats roundStats in roundStatsList)
								{
									await ProcessRoundStats(matchInfo, roundStats, demoUrlList);
								}
							}
						}
					}
					catch (Exception e)
					{
						Logger.Instance.Log(e);
					}
				}
			}

			return demoUrlList;
		}

		public void WriteChatFile(Demo demo, string filePath)
		{
			File.WriteAllText(filePath, string.Join(Environment.NewLine, demo.ChatMessageList.ToArray()));
		}

		public async Task<string> GetShareCode(Demo demo)
		{
			string shareCode = string.Empty;
			string infoFilePath = demo.Path + ".info";
			if (!File.Exists(infoFilePath)) return shareCode;

			using (FileStream file = File.OpenRead(infoFilePath))
			{
				try
				{
					CDataGCCStrike15_v2_MatchInfo matchInfo = await Task.Run(() => Serializer.Deserialize<CDataGCCStrike15_v2_MatchInfo>(file));
					UInt64 matchId = matchInfo.matchid;
					UInt32 tvPort = matchInfo.watchablematchinfo.tv_port;
					UInt64 reservationId;

					// old definition
					if (matchInfo.roundstats_legacy != null)
					{
						reservationId = matchInfo.roundstats_legacy.reservationid;
					}
					else
					{
						// new definition
						List<CMsgGCCStrike15_v2_MatchmakingServerRoundStats> roundStatsList = matchInfo.roundstatsall;
						reservationId = roundStatsList.Last().reservationid;
					}

					shareCode = ShareCode.GenerateShareCode(matchId, reservationId, tvPort);
				}
				catch (Exception e)
				{
					Logger.Instance.Log(e);
				}
			}

			return shareCode;
		}

		/// <summary>
		/// Check if the round stats contains useful information and in this case do the work
		/// 1. Check if the demo archive is still available
		/// 2. Create the .info file
		/// 3. Add the demo to the download list
		/// </summary>
		/// <param name="matchInfo"></param>
		/// <param name="roundStats"></param>
		/// <param name="demoUrlList"></param>
		/// <returns></returns>
		private async Task ProcessRoundStats(CDataGCCStrike15_v2_MatchInfo matchInfo, CMsgGCCStrike15_v2_MatchmakingServerRoundStats roundStats, Dictionary<string, string> demoUrlList)
		{
			string demoName = GetDemoName(matchInfo, roundStats);
			if (roundStats.reservationid != 0 && roundStats.map != null)
			{
				if (await IsDownloadRequired(demoName, roundStats.map))
				{
					if (SerializeMatch(matchInfo, demoName))
					{
						demoUrlList.Add(demoName, roundStats.map);
					}
				}
			}
		}

		/// <summary>
		/// Create the .info file
		/// </summary>
		/// <param name="matchInfo"></param>
		/// <param name="demoName"></param>
		/// <returns></returns>
		private bool SerializeMatch(CDataGCCStrike15_v2_MatchInfo matchInfo, string demoName)
		{
			string infoFilePath = DownloadFolderPath + Path.DirectorySeparatorChar + demoName + ".dem.info";
			try
			{
				using (FileStream fs = File.Create(infoFilePath))
				{
					Serializer.Serialize(fs, matchInfo);
				}
			}
			catch (Exception e)
			{
				Logger.Instance.Log(e);
				return false;
			}

			return true;
		}

		/// <summary>
		/// Return the demo name
		/// </summary>
		/// <param name="matchInfo"></param>
		/// <param name="roundStats"></param>
		/// <returns></returns>
		private static string GetDemoName(CDataGCCStrike15_v2_MatchInfo matchInfo,
			CMsgGCCStrike15_v2_MatchmakingServerRoundStats roundStats)
		{
			return "match730_" + string.Format("{0,21:D21}", roundStats.reservationid) + "_"
				+ string.Format("{0,10:D10}", matchInfo.watchablematchinfo.tv_port) + "_"
				+ matchInfo.watchablematchinfo.server_ip;
		}

		/// <summary>
		/// Check if:
		/// - The demo archive is still available
		/// - The .dem and .info files already exists
		/// </summary>
		/// <param name="demoName"></param>
		/// <param name="demoArchiveUrl"></param>
		/// <returns></returns>
		private async Task<bool> IsDownloadRequired(string demoName, string demoArchiveUrl)
		{
			bool result = await CheckIfArchiveIsAvailable(demoArchiveUrl);
			string[] fileList = new DirectoryInfo(DownloadFolderPath).GetFiles().Select(o => o.Name).ToArray();
			if (fileList.Contains(demoName + ".dem") && fileList.Contains(demoName + ".dem.info")) result = false;

			return result;
		}

		/// <summary>
		/// Check if the URL doesn't return a 404
		/// </summary>
		/// <param name="url"></param>
		/// <returns></returns>
		private static async Task<bool> CheckIfArchiveIsAvailable(string url)
		{
			try
			{
				HttpWebRequest request = WebRequest.Create(url) as HttpWebRequest;
				request.Method = "HEAD";
				HttpWebResponse response = await request.GetResponseAsync() as HttpWebResponse;
				response.Close();
				return response.StatusCode == HttpStatusCode.OK;
			}
			catch
			{
				return false;
			}
		}
	}
}
