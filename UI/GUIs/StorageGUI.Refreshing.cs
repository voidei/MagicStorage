﻿using MagicStorage.Common.Systems;
using MagicStorage.CrossMod;
using MagicStorage.Sorting;
using MagicStorage.UI.States;
using System.Collections.Generic;
using System.Linq;
using Terraria.Localization;
using Terraria;
using System;

namespace MagicStorage {
	partial class StorageGUI {
		// Field included for backwards compatibility, but made Obsolete to encourage modders to use the new API
		[Obsolete("Use the SetRefresh() method or RefreshUI property instead", error: true)]
		public static bool needRefresh;

		[Obsolete]
		private static ref bool Obsolete_needRefresh() => ref needRefresh;

		private static bool _refreshUI;
		public static bool RefreshUI {
			get => _refreshUI;
			set => _refreshUI |= value;
		}

		public static bool CurrentlyRefreshing { get; internal set; }

		public static event Action OnRefresh;
		
		private static bool forceFullRefresh;
		public static bool ForceNextRefreshToBeFull {
			get => forceFullRefresh || Obsolete_needRefresh();
			set => forceFullRefresh |= value;
		}

		/// <summary>
		/// Shorthand for setting <see cref="RefreshUI"/> to <see langword="true"/> and also setting <see cref="ForceNextRefreshToBeFull"/>
		/// </summary>
		public static void SetRefresh(bool forceFullRefresh = false) {
			RefreshUI = true;
			ForceNextRefreshToBeFull = forceFullRefresh;
		}

		internal static readonly List<Item> items = new();
		internal static readonly List<List<Item>> sourceItems = new();
		internal static readonly List<bool> didMatCheck = new();

		public static void RefreshItems()
		{
			// Moved to the start of the logic since CheckRefresh() might be called multiple times during refreshing otherwise
			_refreshUI = false;
			Obsolete_needRefresh() = false;

			// Force full refresh if item deletion mode is active
			if (forceFullRefresh || itemDeletionMode)
				itemTypesToUpdate = null;

			// No refreshing required
			if (StoragePlayer.IsStorageEnvironment()) {
				itemTypesToUpdate = null;
				forceFullRefresh = false;
				return;
			}

			if (StoragePlayer.IsStorageCrafting()) {
				CraftingGUI.RefreshItems();
				itemTypesToUpdate = null;
				forceFullRefresh = false;
				return;
			}

			CraftingGUI.recipesToRefresh = null;

			// Prevent inconsistencies after refreshing items
			itemDeletionSlotFocus = -1;

			var storagePage = MagicUI.storageUI.GetPage<StorageUIState.StoragePage>("Storage");

			storagePage?.RequestThreadWait(waiting: true);

			if (CurrentlyRefreshing) {
				activeThread?.Stop();
				activeThread = null;
			}

			if (itemTypesToUpdate is null)
				RefreshAllItems(storagePage);
			else
				RefreshSpecificItems(storagePage);

			itemTypesToUpdate = null;
			forceFullRefresh = false;
		}

		private static void RefreshAllItems(StorageUIState.StoragePage storagePage) {
			if (InitializeThreadContext(storagePage, true) is not ThreadContext thread)
				return;
			
			// Assign the thread context
			AdjustItemCollectionAndAssignToThread(thread, thread.heart.GetStoredItems());

			// Start the thread
			ThreadContext.Begin(thread);
		}

		private static void RefreshSpecificItems(StorageUIState.StoragePage storagePage) {
			if (InitializeThreadContext(storagePage, false) is not ThreadContext thread)
				return;

			thread.state = itemTypesToUpdate;

			// Get the items that need to be updated
			IEnumerable<Item> itemsToUpdate = thread.heart.GetStoredItems().Where(ShouldItemUpdate);

			IEnumerable<Item> source;
			if (thread.filterMode == FilteringOptionLoader.Definitions.Recent.Type) {
				// Recent filter needs the entire storage as context, but the existing collection only has the results from the previous filter
				// If items are removed, this can cause the displayed amount to be not 100, which is undesirable
				// Using the entire storage is fine since only the first 100 items are used
				source = thread.heart.GetStoredItems();
			} else {
				// Reuse the existing collection for better sorting/filtering time
				source = items;
			}

			// Remove the types to update from the collection, then append the items to update
			IEnumerable<Item> collection = source.Where(static i => !ShouldItemUpdate(i)).Concat(itemsToUpdate);

			// Assign the thread context
			AdjustItemCollectionAndAssignToThread(thread, collection);

			// Start the thread
			ThreadContext.Begin(thread);
		}

		private static bool ShouldItemUpdate(Item item) {
			if (activeThread?.state is not HashSet<int> toUpdate)
				return true;

			return toUpdate.Contains(item.type);
		}

		private static void AdjustItemCollectionAndAssignToThread(ThreadContext thread, IEnumerable<Item> source) {
			// Adjust the thread context based on the filter mode
			if (thread.filterMode == FilteringOptionLoader.Definitions.Recent.Type) {
				Dictionary<int, Item> stored = source.GroupBy(x => x.type).ToDictionary(x => x.Key, x => x.First());

				IEnumerable<Item> toFilter = thread.heart.UniqueItemsPutHistory.Reverse().Where(x => stored.ContainsKey(x.type)).Select(x => stored[x.type]);

				thread.context = new(toFilter);
			} else {
				thread.context = new(source);
			}
		}

		private static void SortAndFilter(ThreadContext thread) {
			// Each DoFiltering does: SortAndFilter, favorite checks, adding items, adding source items
			// Each SortAndFilter does: DoFiltering for items, Aggregate, DoFiltering for source items, DoSorting for source items, DoSorting for items
			thread.InitTaskSchedule(9, "Loading items");

			DoFiltering(thread);
			
			bool didDefault = false;

			// now if nothing found we disable filters one by one
			if (thread.searchText.Trim().Length > 0)
			{
				if (items.Count == 0 && thread.filterMode != FilteringOptionLoader.Definitions.All.Type)
				{
					NetHelper.Report(true, "No items passed the filter.  Attempting filter with All setting");

					// search all categories
					thread.filterMode = FilteringOptionLoader.Definitions.All.Type;

					MagicUI.lastKnownSearchBarErrorReason = Language.GetTextValue("Mods.MagicStorage.Warnings.StorageDefaultToAllItems");
					didDefault = true;

					thread.ResetTaskCompletion();

					DoFiltering(thread);
				}

				if (items.Count == 0 && thread.modSearch != ModSearchBox.ModIndexAll)
				{
					NetHelper.Report(true, "No items passed the filter.  Attempting filter with All Mods setting");

					// search all mods
					thread.modSearch = ModSearchBox.ModIndexAll;

					MagicUI.lastKnownSearchBarErrorReason = Language.GetTextValue("Mods.MagicStorage.Warnings.StorageDefaultToAllMods");
					didDefault = true;

					thread.ResetTaskCompletion();

					DoFiltering(thread);
				}
			}

			if (!didDefault)
				MagicUI.lastKnownSearchBarErrorReason = null;
		}

		private static bool filterOutFavorites;

		private static void DoFiltering(ThreadContext thread)
		{
			try {
				NetHelper.Report(true, "Applying item filters...");

				if (thread.filterMode == FilteringOptionLoader.Definitions.Recent.Type)
				{
					if (thread.sortMode == SortingOptionLoader.Definitions.Default.Type)
						thread.sortMode = -1;

					thread.filterMode = FilteringOptionLoader.Definitions.All.Type;

					thread.context.items = ItemSorter.SortAndFilter(thread, 100, aggregate: !itemDeletionMode);
				}
				else
				{
					thread.context.items = ItemSorter.SortAndFilter(thread, aggregate: !itemDeletionMode);
				}

				thread.CompleteOneTask();

				if (MagicStorageConfig.CraftingFavoritingEnabled) {
					thread.context.items = thread.context.items.OrderByDescending(static x => x.favorited ? 1 : 0);
					thread.context.sourceItems = thread.context.sourceItems.OrderByDescending(static x => x[0].favorited ? 1 : 0);
				}

				thread.CompleteOneTask();

				filterOutFavorites = thread.onlyFavorites;

				if (thread.state is not null) {
					// Specific IDs were refreshed, meaning the lists weren't cleared.  Clear them now
					items.Clear();
					sourceItems.Clear();
					didMatCheck.Clear();
				}

				items.AddRange(thread.context.items.Where(static x => !MagicStorageConfig.CraftingFavoritingEnabled || !filterOutFavorites || x.favorited));

				thread.CompleteOneTask();

				sourceItems.AddRange(thread.context.sourceItems.Where(static x => !MagicStorageConfig.CraftingFavoritingEnabled || !filterOutFavorites || x[0].favorited));

				thread.CompleteOneTask();

				NetHelper.Report(true, "Filtering applied.  Item count: " + items.Count);
			} catch when (thread.token.IsCancellationRequested) {
				items.Clear();
				sourceItems.Clear();
				didMatCheck.Clear();
				throw;
			}
		}

		private static void AfterSorting(ThreadContext thread) {
			// Refresh logic in the UIs will only run when this is false
			if (!thread.token.IsCancellationRequested)
				CurrentlyRefreshing = false;

			for (int k = 0; k < items.Count; k++)
				didMatCheck.Add(false);

			// Ensure that race conditions with the UI can't occur
			// QueueMainThreadAction will execute the logic in a very specific place
			Main.QueueMainThreadAction(InvokeOnRefresh);

			MagicUI.storageUI.GetPage<StorageUIState.StoragePage>("Storage")?.RequestThreadWait(waiting: false);
		}
	}
}
