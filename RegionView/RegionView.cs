using Microsoft.Xna.Framework;
using System.Timers;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using TShockAPI.Hooks;

namespace RegionView
{
    public class RegionView : TerrariaPlugin
    {
		public const int NearRange = 100;

		public List<RegionPlayer> Players { get; } = new();

        public static Color[] TextColors { get; } = new[] 
        {
            new Color(244,  93,  93),
            new Color(244, 169,  93),
            new Color(244, 244,  93),
            new Color(169, 244,  93),
            new Color( 93, 244,  93),
            new Color( 93, 244, 169),
            new Color( 93, 244, 244),
            new Color( 93, 169, 244),
            new Color( 93,  93, 244),
            new Color(169,  93, 244),
            new Color(244,  93, 244),
            new Color(244,  93, 169)
        };

        public override string Author
            => "TBC Developers";

        public override string Description
            => "Adds region border view to regions.";

        public override string Name
            => "RegionView";

        public override Version Version
            => new(1, 0);

		private readonly System.Timers.Timer _refreshTimer = new(5000);

		public RegionView(Main game)
            : base(game)
        {
            Order = 1;
        }

		public override void Initialize()
		{
			Commands.ChatCommands.Add(new Command("regionvision.regionview", CommandView, "regionview", "rv")
			{
				AllowServer = false,
				HelpText = "Shows you the boundary of the specified region"
			});

			Commands.ChatCommands.Add(new Command("regionvision.regionview", CommandClear, "regionclear", "rc")
			{
				AllowServer = false,
				HelpDesc = new string[] { "Usage: /rc", "Removes all region borders from your view" }
			});

			Commands.ChatCommands.Add(new Command("regionvision.regionviewnear", CommandViewNearby, "regionviewnear", "rvn")
			{
				AllowServer = false,
				HelpText = "Turns on or off automatic showing of regions near you"
			});

			GetDataHandlers.TileEdit += HandlerList<GetDataHandlers.TileEditEventArgs>.Create(OnTileEdit!, HandlerPriority.High, false);

			ServerApi.Hooks.ServerJoin.Register(this, OnPlayerJoin);
			ServerApi.Hooks.ServerLeave.Register(this, OnPlayerLeave);

			PlayerHooks.PlayerCommand += OnPlayerCommand;
			RegionHooks.RegionCreated += RegionCreated;
			RegionHooks.RegionDeleted += RegionDeleted;

			_refreshTimer.AutoReset = false;

			_refreshTimer.Elapsed += (x, _) => RefreshRegions();
		}

		private void RegionDeleted(RegionHooks.RegionDeletedEventArgs args)
		{
			if (args.Region.WorldID != Main.worldID.ToString()) return;

			// If any players were viewing this region, clear its border.
			lock (Players)
			{
				foreach (var player in Players)
				{
					for (var i = 0; i < player.Regions.Count; i++)
					{
						var region = player.Regions[i];
						if (region.Name.Equals(args.Region.Name))
						{
							player.TSPlayer.SendMessage("Region " + region.Name + " has been deleted.", TextColors[region.Color - 13]);
							region.Refresh(player.TSPlayer);
							player.Regions.RemoveAt(i);

							foreach (var region2 in player.Regions)
								region2.SetFakeTiles();
							foreach (var region2 in player.Regions)
								region2.Refresh(player.TSPlayer);
							foreach (var region2 in player.Regions.Reverse<Region>())
								region2.UnsetFakeTiles();

							break;
						}
					}
				}
			}
		}

		private void RegionCreated(RegionHooks.RegionCreatedEventArgs args)
		{
			_refreshTimer.Stop();
			RefreshRegions();
		}

		public RegionPlayer? FindPlayer(int index) 
			=> Players.FirstOrDefault(p => p.Index == index);

		private void CommandView(CommandArgs args)
		{
			TShockAPI.DB.Region? tRegion = null;
			var matches = new List<TShockAPI.DB.Region>();

			if (args.Parameters.Count < 1)
			{
				args.Player.SendErrorMessage("Usage: /regionview <region name>");
				return;
			}

			// Find the specified region.
			for (var pass = 1; pass <= 3 && tRegion == null && matches.Count == 0; pass++)
			{
				foreach (var _tRegion in TShock.Regions.Regions)
				{
					switch (pass)
					{
						case 1:  // Pass 1: exact match
							if (_tRegion.Name == args.Parameters[0])
							{
								tRegion = _tRegion;
								break;
							}
							else if (_tRegion.Name.Equals(args.Parameters[0], StringComparison.OrdinalIgnoreCase))
								matches.Add(_tRegion);
							break;
						case 2:  // Pass 2: case-sensitive partial match
							if (_tRegion.Name.StartsWith(args.Parameters[0]))
								matches.Add(_tRegion);
							break;
						case 3:  // Pass 3: case-insensitive partial match
							if (_tRegion.Name.StartsWith(args.Parameters[0], StringComparison.OrdinalIgnoreCase))
								matches.Add(_tRegion);
							break;
					}
					if (tRegion != null) 
						break;
				}
			}

			if (tRegion == null)
			{
				if (matches.Count == 1)
				{
					tRegion = matches[0];
				}
				else if (matches.Count == 0)
				{
					args.Player.SendErrorMessage("No such region exists.");
					return;
				}
				else if (matches.Count > 5)
				{
					args.Player.SendErrorMessage("Multiple matching regions were found: {0} and {1} more. Please be more specific.", string.Join(", ", matches.Take(5).Select(r => r.Name)), matches.Count - 5);
					return;
				}
				else if (matches.Count > 1)
				{
					args.Player.SendErrorMessage("Multiple matching regions were found: {0}. Please be more specific.", string.Join(", ", matches.Select(r => r.Name)));
					return;
				}
			}

			if (tRegion!.Area.Width < 0 || tRegion.Area.Height < 0)
			{
				args.Player.SendErrorMessage("Region {0} contains no tiles. (Found dimensions: {1} × {2})\nUse [c/FF8080:/region resize] to fix it.", tRegion.Name, tRegion.Area.Width, tRegion.Area.Height);
				return;
			}

			lock (Players)
			{
				var player = FindPlayer(args.Player.Index);

				if (player == null) 
					return;

				// Register this region.
				var region = player.Regions.FirstOrDefault(r => r.Name == tRegion.Name);

				if (region == null)
					region = new Region(tRegion.Name, tRegion.Area);
				else
					player.Regions.Remove(region);

				foreach (var _region in player.Regions)
					_region.SetFakeTiles();

				if (region.ShowArea != region.Area) 
					region.Refresh(player.TSPlayer);

				player.Regions.Add(region);

				region.CalculateArea(args.Player);
				region.SetFakeTiles();
				region.Refresh(player.TSPlayer);

				foreach (var _region in player.Regions.Reverse<Region>())
					_region.UnsetFakeTiles();

				var message = "You are now viewing " + region.Name + ".";
				// Show how large the region is if it's large.
				if (tRegion.Area.Width >= Region.MaximumSize || tRegion.Area.Height >= Region.MaximumSize)
				{
					int num; int num2;
					if (tRegion.Area.Bottom < args.Player.TileY)
					{
						num = args.Player.TileY - tRegion.Area.Bottom;
						message += " Borders are " + num + (num == 1 ? " tile" : " tiles") + " above you";
					}
					else if (tRegion.Area.Top > args.Player.TileY)
					{
						num = tRegion.Area.Top - args.Player.TileY;
						message += " Borders are " + (tRegion.Area.Top - args.Player.TileY) + (num == 1 ? " tile" : " tiles") + " below you";
					}
					else
					{
						num = args.Player.TileY - tRegion.Area.Top;
						num2 = tRegion.Area.Bottom - args.Player.TileY;
						message += " Borders are " + (args.Player.TileY - tRegion.Area.Top) + (num == 1 ? " tile" : " tiles") + " above, " +
							(tRegion.Area.Bottom - args.Player.TileY) + (num2 == 1 ? " tile" : " tiles") + " below you";
					}
					if (tRegion.Area.Right < args.Player.TileX)
					{
						num = args.Player.TileX - tRegion.Area.Right;
						message += ", " + (args.Player.TileX - tRegion.Area.Right) + (num == 1 ? " tile" : " tiles") + " west of you.";
					}
					else if (tRegion.Area.Left > args.Player.TileX)
					{
						num = tRegion.Area.Left - args.Player.TileX;
						message += ", " + (tRegion.Area.Left - args.Player.TileX) + (num == 1 ? " tile" : " tiles") + " east of you.";
					}
					else
					{
						num = args.Player.TileX - tRegion.Area.Left;
						num2 = tRegion.Area.Right - args.Player.TileX;
						message += ", " + (args.Player.TileX - tRegion.Area.Left) + (num == 1 ? " tile" : " tiles") + " west, " +
							(tRegion.Area.Right - args.Player.TileX) + (num2 == 1 ? " tile" : " tiles") + " east of you.";
					}
				}
				args.Player.SendMessage(message, TextColors[region.Color - 13]);

				_refreshTimer.Interval = 7000;
				_refreshTimer.Enabled = true;
			}
		}

		private void CommandClear(CommandArgs args)
		{
			lock (Players)
			{
				var player = FindPlayer(args.Player.Index);
				if (player == null) 
					return;

				player.IsViewingNearby = false;
				ClearRegions(player);
			}
		}

		private void CommandViewNearby(CommandArgs args)
		{
			lock (Players)
			{
				var player = FindPlayer(args.Player.Index);

				if (player == null) 
					return;

				if (player.IsViewingNearby)
				{
					player.IsViewingNearby = false;
					args.Player.SendInfoMessage("You are no longer viewing regions near you.");
				}
				else
				{
					player.IsViewingNearby = true;
					args.Player.SendInfoMessage("You are now viewing regions near you.");

					_refreshTimer.Interval = 1500;
					_refreshTimer.Enabled = true;
				}
			}
		}

		public static void ClearRegions(RegionPlayer player)
		{
			foreach (var region in player.Regions)
				region.Refresh(player.TSPlayer);

			player.Regions.Clear();
		}

		private void OnTileEdit(object sender, GetDataHandlers.TileEditEventArgs e)
		{
			if (e.Action is > GetDataHandlers.EditAction.KillTileNoItem or GetDataHandlers.EditAction.KillWall) 
				return;

			if (e.Action == GetDataHandlers.EditAction.PlaceTile && e.EditData == Terraria.ID.TileID.MagicalIceBlock) 
				return;

			lock (Players)
			{
				var player = this.FindPlayer(e.Player.Index);
				if (player == null)
					return;

				if (player.Regions.Count == 0)
					return;

				// Stop the edit if a phantom tile is the only thing making it possible.
				foreach (var region in player.Regions)
				{
					// Clear the region borders if they break one of the phantom ice blocks.
					if ((e.Action == GetDataHandlers.EditAction.KillTile || e.Action == GetDataHandlers.EditAction.KillTileNoItem) && (Main.tile[e.X, e.Y] == null || !Main.tile[e.X, e.Y].active()) &&
						e.X >= region.ShowArea.Left - 1 && e.X <= region.ShowArea.Right + 1 && e.Y >= region.ShowArea.Top - 1 && e.Y <= region.ShowArea.Bottom + 1 &&
						!(e.X >= region.ShowArea.Left + 2 && e.X <= region.ShowArea.Right - 2 && e.Y >= region.ShowArea.Top + 2 && e.Y <= region.ShowArea.Bottom - 2))
					{
						e.Handled = true;
						//clearRegions(player);
						break;
					}
					if ((e.Action == GetDataHandlers.EditAction.PlaceTile || e.Action == GetDataHandlers.EditAction.PlaceWall) && !TileValidityCheck(region, e.X, e.Y, e.Action))
					{
						e.Handled = true;
						player.TSPlayer.SendData(PacketTypes.TileSendSquare, "", 1, e.X, e.Y, 0, 0);
						if (e.Action == GetDataHandlers.EditAction.PlaceTile) GiveTile(player, e);
						if (e.Action == GetDataHandlers.EditAction.PlaceWall) GiveWall(player, e);
						break;
					}
				}

				if (e.Handled) 
					ClearRegions(player);
			}
		}

		private void OnPlayerJoin(JoinEventArgs e)
		{
			lock (Players)
				Players.Add(new(e.Who));
		}

		private void OnPlayerLeave(LeaveEventArgs e)
		{
			lock (Players)
				for (var i = 0; i < Players.Count; i++)
				{
					if (Players[i].Index == e.Who)
					{
						Players.RemoveAt(i);
						break;
					}
				}
		}

		private void OnPlayerCommand(PlayerCommandEventArgs e)
		{
			if (e.Parameters.Count >= 2 && e.CommandName.ToLower() == "region" && new[] { "delete", "resize", "expand" }.Contains(e.Parameters[0].ToLower()))
			{
				if (Commands.ChatCommands.Any(c => c.HasAlias("region") && c.CanRun(e.Player)))
					_refreshTimer.Interval = 1500;
			}
		}

		private void Tick(object sender, ElapsedEventArgs e) 
			=> this.RefreshRegions();

		private void RefreshRegions()
		{
			var anyRegions = false;

			// Check for regions that have changed.
			lock (Players)
			{
				foreach (var player in Players)
				{
					var refreshFlag = false;

					for (var i = 0; i < player.Regions.Count; i++)
					{
						var region = player.Regions[i];
						var tRegion = TShock.Regions.GetRegionByName(region.Name);

						if (tRegion == null)
						{
							// The region was removed.
							refreshFlag = true;
							region.Refresh(player.TSPlayer);
							player.Regions.RemoveAt(i--);
						}
						else
						{
							var newArea = tRegion.Area;
							if (!region.Command && (!player.IsViewingNearby || !IsPlayerNearby(player.TSPlayer, region.Area)))
							{
								// The player is no longer near the region.
								refreshFlag = true;
								region.Refresh(player.TSPlayer);
								player.Regions.RemoveAt(i--);
							}
							else
							if (newArea != region.Area)
							{
								// The region was resized.
								if (newArea.Width < 0 || newArea.Height < 0)
								{
									refreshFlag = true;
									region.Refresh(player.TSPlayer);
									player.Regions.RemoveAt(i--);
								}
								else
								{
									anyRegions = true;
									refreshFlag = true;
									region.Refresh(player.TSPlayer);
									region.Area = newArea;
									region.CalculateArea(player.TSPlayer);
								}
							}
							else
							{
								anyRegions = true;
							}
						}
					}

					if (player.IsViewingNearby)
					{
						anyRegions = true;

						// Search for nearby regions
						foreach (var tRegion in TShock.Regions.Regions)
						{
							if (tRegion.WorldID == Main.worldID.ToString() && tRegion.Area.Width >= 0 && tRegion.Area.Height >= 0)
							{
								if (IsPlayerNearby(player.TSPlayer, tRegion.Area))
								{
									if (!player.Regions.Any(r => r.Name == tRegion.Name))
									{
										refreshFlag = true;
										var region = new Region(tRegion.Name, tRegion.Area, false);
										region.CalculateArea(player.TSPlayer);
										player.Regions.Add(region);
										player.TSPlayer.SendMessage("You see region " + region.Name + ".", TextColors[region.Color - 13]);
									}
								}
							}
						}
					}

					if (refreshFlag)
					{
						foreach (var region in player.Regions)
							region.SetFakeTiles();
						foreach (var region in player.Regions)
							region.Refresh(player.TSPlayer);
						foreach (var region in player.Regions.Reverse<Region>())
							region.UnsetFakeTiles();
					}
				}
			}

			if (anyRegions)
			{
				_refreshTimer.Interval = 7000;
				_refreshTimer.Enabled = true;
			}
		}

		public static bool IsPlayerNearby(TSPlayer tPlayer, Rectangle area)
		{
			var playerX = (int)(tPlayer.X / 16);
			var playerY = (int)(tPlayer.Y / 16);

			return playerX >= area.Left - NearRange &&
					playerX <= area.Right + NearRange &&
					playerY >= area.Top - NearRange &&
					playerY <= area.Bottom + NearRange;
		}

		public static bool TileValidityCheck(Region region, int x, int y, GetDataHandlers.EditAction editType)
		{
			// Check if there's a wall or another tile next to this tile.
			if (editType == GetDataHandlers.EditAction.PlaceWall)
			{
				if (Main.tile[x, y] != null && Main.tile[x, y].active())
					return true;

				if (Main.tile[x - 1, y] != null && ((Main.tile[x - 1, y].active() && !Main.tileNoAttach[Main.tile[x - 1, y].type]) || Main.tile[x - 1, y].wall > 0))
					return true;

				if (Main.tile[x + 1, y] != null && ((Main.tile[x + 1, y].active() && !Main.tileNoAttach[Main.tile[x + 1, y].type]) || Main.tile[x + 1, y].wall > 0))
					return true;

				if (Main.tile[x, y - 1] != null && ((Main.tile[x, y - 1].active() && !Main.tileNoAttach[Main.tile[x, y - 1].type]) || Main.tile[x, y - 1].wall > 0))
					return true;

				if (Main.tile[x, y + 1] != null && ((Main.tile[x, y + 1].active() && !Main.tileNoAttach[Main.tile[x, y + 1].type]) || Main.tile[x, y + 1].wall > 0))
					return true;
			}
			else
			{
				if (Main.tile[x, y] != null && Main.tile[x, y].wall > 0)
					return true;

				if (Main.tile[x - 1, y] != null && Main.tile[x - 1, y].wall > 0)
					return true;

				if (Main.tile[x + 1, y] != null && Main.tile[x + 1, y].wall > 0)
					return true;

				if (Main.tile[x, y - 1] != null && Main.tile[x, y - 1].wall > 0)
					return true;

				if (Main.tile[x, y + 1] != null && Main.tile[x, y + 1].wall > 0)
					return true;

				if (Main.tile[x - 1, y] != null && Main.tile[x - 1, y].active() && !Main.tileNoAttach[Main.tile[x - 1, y].type])
					return true;

				if (Main.tile[x + 1, y] != null && Main.tile[x + 1, y].active() && !Main.tileNoAttach[Main.tile[x + 1, y].type])
					return true;

				if (Main.tile[x, y - 1] != null && Main.tile[x, y - 1].active() && !Main.tileNoAttach[Main.tile[x, y - 1].type])
					return true;

				if (Main.tile[x, y - 1] != null && Main.tile[x, y + 1].active() && !Main.tileNoAttach[Main.tile[x, y + 1].type])
					return true;
			}

			// Check if this tile is next to a region boundary.
			return x < region.ShowArea.Left - 1 || x > region.ShowArea.Right + 1 || y < region.ShowArea.Top - 1 || y > region.ShowArea.Bottom + 1 ||
				x >= region.ShowArea.Left + 2 && x <= region.ShowArea.Right - 2 && y >= region.ShowArea.Top + 2 && y <= region.ShowArea.Bottom - 2;
		}

		public static void GiveTile(RegionPlayer player, GetDataHandlers.TileEditEventArgs e)
		{
			var item = new Item();
			var found = false;

			for (var i = 1; i <= Terraria.ID.ItemID.Count; i++)
			{
				item.SetDefaults(i, true);
				if (item.createTile == e.EditData && item.placeStyle == e.Style)
				{
					if (item.tileWand != -1) item.SetDefaults(item.tileWand, true);
					found = true;
					break;
				}
			}

			if (found)
				GiveItem(player, item);
		}

		public static void GiveWall(RegionPlayer player, GetDataHandlers.TileEditEventArgs e)
		{
			var item = new Item(); var found = false;
			for (var i = 1; i <= Terraria.ID.ItemID.Count; i++)
			{
				item.SetDefaults(i, true);
				if (item.createWall == e.EditData)
				{
					found = true;
					break;
				}
			}
			if (found)
			{
				item.stack = 1;
				GiveItem(player, item);
			}
		}

		public static void GiveItem(RegionPlayer player, Item item)
			=> player.TSPlayer.GiveItem(item.type, 1);
	}
}