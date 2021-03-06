using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using EDEngineer.Localization;
using EDEngineer.Models;
using EDEngineer.Models.Operations;
using EDEngineer.Models.Utils;
using EDEngineer.Properties;
using EDEngineer.Utils;
using EDEngineer.Utils.System;
using EDEngineer.Views.Notifications;
using Newtonsoft.Json;
using NodaTime;
using ThresholdsManagerWindow = EDEngineer.Views.Popups.Thresholds.ThresholdsManagerWindow;

namespace EDEngineer.Views
{
    public class CommanderViewModel : INotifyPropertyChanged, IDisposable
    {
        public string CommanderName { get; }
        public State State { get; }
        public BlueprintFilters Filters { get; private set; }
        public ObservableCollection<Entry> HighlightedEntryData { get; } = new ObservableCollection<Entry>();

        public ShoppingListViewModel ShoppingList => shoppingList;

        private readonly JournalEntryConverter journalEntryConverter;
        private readonly BlueprintConverter blueprintConverter;

        private readonly HashSet<Blueprint> favoritedBlueprints = new HashSet<Blueprint>();
        private Instant lastUpdate = Instant.MinValue;
        private ShoppingListViewModel shoppingList;
        private readonly CommanderNotifications commanderNotifications;

        public Instant LastUpdate
        {
            get { return lastUpdate; }
            set
            {
                if (value == lastUpdate)
                    return;
                lastUpdate = value;
                OnPropertyChanged();
            }
        }
        
        public event PropertyChangedEventHandler PropertyChanged;
        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void LoadState(IEnumerable<string> events)
        {
            commanderNotifications?.UnsubscribeNotifications();
            State.InitLoad();
            // Clear state:
            
            State.Cargo.ToList().ForEach(k => State.IncrementCargo(k.Value.Data.Name, -1 * k.Value.Count));
            LastUpdate = Instant.MinValue;

            ApplyEventsToSate(events);
            ThresholdsManagerWindow.InitThresholds(State.Cargo);
            commanderNotifications?.SubscribeNotifications();

            State.Cargo.RefreshSort();
            State.CompleteLoad();
        }

        public CommanderViewModel(string commanderName, IEnumerable<string> logs, Languages languages, List<EntryData> entryDatas)
        {
            CommanderName = commanderName;

            var converter = new ItemNameConverter(entryDatas);

            State = new State(entryDatas, languages, SettingsManager.Comparer);

            commanderNotifications = new CommanderNotifications(State);

            journalEntryConverter = new JournalEntryConverter(converter, State.Cargo, languages);
            blueprintConverter = new BlueprintConverter(State.Cargo);
            LoadBlueprints(languages);

            languages.PropertyChanged += (o, e) => OnPropertyChanged(nameof(Filters));

            LoadState(logs);

            var datas = State.Cargo.Select(c => c.Value.Data);
            var ingredientUsed = State.Blueprints.SelectMany(blueprint => blueprint.Ingredients);
            var ingredientUsedNames = ingredientUsed.Select(ingredient => ingredient.Entry.Data.Name).Distinct();
            var unusedIngredients = datas.Where(data => !ingredientUsedNames.Contains(data.Name));

            foreach (var data in unusedIngredients)
            {
                data.Unused = true;
            }
        }

        public JournalEntry UserChange(Entry entry, int change)
        {
            var logEntry = new JournalEntry
            {
                JournalOperation = new ManualChangeOperation
                {
                    Count = change,
                    JournalEvent = JournalEvent.ManualUserChange,
                    Name = entry.Data.Name
                },
                Timestamp = SystemClock.Instance.GetCurrentInstant()
            };

            var json = JsonConvert.SerializeObject(logEntry, journalEntryConverter);

            logEntry.OriginalJson = json;

            MutateState(logEntry);

            return logEntry;
        }

        public void ApplyEventsToSate(IEnumerable<string> allLogs)
        {
            var settings = new JsonSerializerSettings()
            {
                Converters = new List<JsonConverter>() { journalEntryConverter },
                Error = (o, e) => e.ErrorContext.Handled = true
            };

            var entries = allLogs.Select(l => JsonConvert.DeserializeObject<JournalEntry>(l, settings))
                .Where(e => e?.Relevant == true)
                .OrderBy(e => e.Timestamp)
                .ToList();

            foreach (var entry in entries.Where(entry => entry.Timestamp >= LastUpdate).ToList())
            {
                MutateState(entry);
            }
        }

        private void MutateState(JournalEntry entry)
        {
            State.Operations.AddLast(entry);
            entry.JournalOperation.Mutate(State);
            LastUpdate = Instant.Max(LastUpdate, entry.Timestamp);
        }

        public ICollectionView FilterView(MainWindowViewModel parentViewModel, Kind kind, CollectionViewSource source)
        {
            source.Filter += (o, e) =>
            {
                var entry = ((KeyValuePair<string, Entry>)e.Item).Value;

                e.Accepted = (entry.Data.Kind == kind || entry.Data.Kind == Kind.Unknown) &&
                       (parentViewModel.MaterialSubkindFilter == null || entry.Data.Kind == Kind.Data || parentViewModel.MaterialSubkindFilter == entry.Data.Subkind) &&
                       (parentViewModel.ShowZeroes || entry.Count != 0) &&
                       (!parentViewModel.ShowOnlyForFavorites || favoritedBlueprints.Any(b => b.Ingredients.Any(i => i.Entry == entry)));
            };

            parentViewModel.PropertyChanged += (o, e) =>
            {
                if (e.PropertyName == nameof(parentViewModel.ShowZeroes) || e.PropertyName == nameof(parentViewModel.ShowOnlyForFavorites))
                {
                    source.View.Refresh();
                }
            };

            State.Blueprints.ForEach(b => b.PropertyChanged += (o, e) =>
            {
                if (parentViewModel.ShowOnlyForFavorites && e.PropertyName == "Favorite")
                {
                    Application.Current.Dispatcher.Invoke(source.View.Refresh);
                }
            });

            return source.View;
        }

        private void LoadBlueprints(ILanguage languages)
        {
            var blueprintsJson = IOUtils.GetBlueprintsJson();
            var blueprints =
                JsonConvert.DeserializeObject<List<Blueprint>>(blueprintsJson, blueprintConverter)
                           .Where(b => b.Ingredients.Any());


            State.Blueprints = new List<Blueprint>(blueprints);
            if (Settings.Default.Favorites == null)
            {
                Settings.Default.Favorites = new StringCollection();
            }

            if (Settings.Default.Ignored == null)
            {
                Settings.Default.Ignored = new StringCollection();
            }

            if (Settings.Default.ShoppingList == null)
            {
                Settings.Default.ShoppingList = new StringCollection();
            }

            foreach (var blueprint in State.Blueprints)
            {
                var text = $"{CommanderName}:{blueprint}";

                if (Settings.Default.Favorites.Contains(text))
                {
                    blueprint.Favorite = true;
                    favoritedBlueprints.Add(blueprint);

                    if (Settings.Default.Favorites.Contains($"{blueprint}"))
                    {
                        Settings.Default.Favorites.Remove($"{blueprint}");
                        Settings.Default.Save();
                    }
                }
                else if (Settings.Default.Favorites.Contains($"{blueprint}"))
                {
                    blueprint.Favorite = true;
                    favoritedBlueprints.Add(blueprint);
                    Settings.Default.Favorites.Remove($"{blueprint}");
                    Settings.Default.Favorites.Add(text);
                    Settings.Default.Save();
                }

                if (Settings.Default.Ignored.Contains(text))
                {
                    blueprint.Ignored = true;

                    if (Settings.Default.Ignored.Contains($"{blueprint}"))
                    {
                        Settings.Default.Ignored.Remove($"{blueprint}");
                        Settings.Default.Save();
                    }
                }
                else if (Settings.Default.Ignored.Contains($"{blueprint}"))
                {
                    blueprint.Ignored = true;
                    Settings.Default.Ignored.Remove($"{blueprint}");
                    Settings.Default.Ignored.Add(text);
                    Settings.Default.Save();
                }

                blueprint.ShoppingListCount = Settings.Default.ShoppingList.Cast<string>().Count(l => l == text);

                blueprint.PropertyChanged += (o, e) =>
                {
                    if (e.PropertyName == "Favorite")
                    {
                        if (blueprint.Favorite)
                        {
                            Settings.Default.Favorites.Add($"{CommanderName}:{blueprint}");
                            favoritedBlueprints.Add(blueprint);
                        }
                        else
                        {
                            Settings.Default.Favorites.Remove($"{CommanderName}:{blueprint}");
                            favoritedBlueprints.Remove(blueprint);
                        }

                        Settings.Default.Save();
                    }
                    else if (e.PropertyName == "Ignored")
                    {
                        if (blueprint.Ignored)
                        {
                            Settings.Default.Ignored.Add($"{CommanderName}:{blueprint}");
                        }
                        else
                        {
                            Settings.Default.Ignored.Remove($"{CommanderName}:{blueprint}");
                        }

                        Settings.Default.Save();
                    }
                    else if (e.PropertyName == "ShoppingListCount")
                    {
                        while (Settings.Default.ShoppingList.Contains(text))
                        {
                            Settings.Default.ShoppingList.Remove(text);
                        }

                        for (var i = 0; i < blueprint.ShoppingListCount; i++)
                        {
                            Settings.Default.ShoppingList.Add(text);
                        }

                        Settings.Default.Save();
                    }
                };
            }

            Filters = new BlueprintFilters(languages, State.Blueprints);

            shoppingList = new ShoppingListViewModel(State.Blueprints, languages);
        }


        public void ShoppingListChange(Blueprint blueprint, int i)
        {
            if (blueprint.ShoppingListCount + i >= 0)
            {
                blueprint.ShoppingListCount += i;

                OnPropertyChanged(nameof(ShoppingList));
                OnPropertyChanged(nameof(ShoppingListItem));
            }
        }

        public int ShoppingListItem => 0;

        public override string ToString()
        {
            return $"CMDR {CommanderName}";
        }

        public void Dispose()
        {
            commanderNotifications?.Dispose();
        }

        public void ToggleHighlight(Entry entry)
        {
            entry.Highlighted = !entry.Highlighted;

            if (entry.Highlighted)
            {
                HighlightedEntryData.Add(entry);
            }
            else
            {
                HighlightedEntryData.Remove(entry);
            }

            Settings.Default.Save();
        }

        public void HighlightShoppingListIngredient(List<BlueprintIngredient> ingredients, Blueprint blueprint, bool highlighted)
        {
            foreach (
                var ingredient in
                    blueprint.Ingredients.Join(ingredients,
                        ingredient => ingredient.Entry.Data.Name,
                        ingredient => ingredient.Entry.Data.Name,
                        (_, ingredient) => ingredient))
            {
                ingredient.ShoppingListHighlighted = highlighted;
            }

            blueprint.ShoppingListHighlighted = highlighted;
        }

        public void HighlightShoppingListBlueprint(List<Tuple<Blueprint, int>> blueprints, BlueprintIngredient ingredient, bool highlighted)
        {
            foreach(var blueprint in blueprints.Select(i => i.Item1).Where(b => b.Ingredients.Any(i => i.Entry.Data.Name == ingredient.Entry.Data.Name)))
            {
                blueprint.ShoppingListHighlighted = highlighted;
            }

            ingredient.ShoppingListHighlighted = highlighted;
        }

        public void RefreshShoppingList()
        {
            // relevant when live reloading a commander, because WPF didn't bind upon creating the object:
            OnPropertyChanged(nameof(ShoppingList));
            OnPropertyChanged(nameof(ShoppingListItem));
        }
    }
}