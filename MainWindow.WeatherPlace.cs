using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DynamicIsland;

public partial class MainWindow
{
    string _weatherCountryCode = "", _weatherCountryName = "";
    bool _choosingWeatherCountry, _settingWeatherSearch, _weatherPlaceEditing;
    CancellationTokenSource? _weatherSearch;
    IntPtr _beforeWeatherSearch;

    void RefreshWeatherLocationSettings(bool animate)
    {
        WeatherLocationMode.Set(Settings.WeatherManual ? 1 : 0, animate);
        WeatherPlaceRow.Visibility = Settings.WeatherManual ? Visibility.Visible : Visibility.Collapsed;
        WeatherPlaceSummary.Text = Settings.WeatherPlace is { } place
            ? $"{place.Country} · {place.Name}" : "Выбрать страну и город";
    }

    void WeatherLocationMode_Click(object sender, RoutedEventArgs e)
    {
        bool manual = WeatherLocationMode.PickUnderPointer() == 1;
        if (manual == Settings.WeatherManual) return;
        Settings.WeatherManual = manual;
        RefreshWeatherLocationSettings(true);
        _ = _weather.RefreshAsync(true);
    }

    void WeatherPlaceRow_Click(object sender, RoutedEventArgs e) => OpenWeatherPlace();

    void OpenWeatherPlace()
    {
        WeatherPlace? selected = Settings.WeatherPlace;
        _weatherCountryCode = selected?.CountryCode ?? RegionInfo.CurrentRegion.TwoLetterISORegionName;
        _weatherCountryName = selected?.Country
            ?? WeatherPlaces.Countries.FirstOrDefault(c => c.Code == _weatherCountryCode)?.Name ?? "";
        _choosingWeatherCountry = _weatherCountryName.Length == 0;
        ShowPanel(Panel.WeatherPlace);
        _weatherPlaceEditing = true;
        _beforeWeatherSearch = Native.ForegroundWindow;
        Native.AllowTextInput(_hwnd, true);
        Focusable = true;
        Activate();
        SetWeatherSearch("");
        RefreshWeatherPlaceSearch();
        Dispatcher.InvokeAsync(() =>
        {
            if (_panel == Panel.WeatherPlace) WeatherPlaceSearch.Focus();
        });
    }

    void EndWeatherPlaceEdit()
    {
        _weatherSearch?.Cancel();
        _weatherSearch = null;
        if (!_weatherPlaceEditing) return;
        _weatherPlaceEditing = false;
        Keyboard.ClearFocus();
        Focusable = false;
        Native.AllowTextInput(_hwnd, false);
        Native.RestoreForeground(_hwnd, _beforeWeatherSearch);
    }

    void WeatherPlaceBack_Click(object sender, RoutedEventArgs e)
    {
        if (_choosingWeatherCountry && _weatherCountryName.Length > 0)
        {
            _choosingWeatherCountry = false;
            SetWeatherSearch("");
            RefreshWeatherPlaceSearch();
        }
        else ShowPanel(Panel.Settings);
    }

    void WeatherCountry_Click(object sender, RoutedEventArgs e)
    {
        _choosingWeatherCountry = !_choosingWeatherCountry || _weatherCountryName.Length == 0;
        SetWeatherSearch("");
        RefreshWeatherPlaceSearch();
        WeatherPlaceSearch.Focus();
    }

    void SetWeatherSearch(string text)
    {
        _settingWeatherSearch = true;
        WeatherPlaceSearch.Text = text;
        _settingWeatherSearch = false;
    }

    void WeatherPlaceSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingWeatherSearch && _weatherPlaceEditing) RefreshWeatherPlaceSearch();
    }

    void WeatherPlaceSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            WeatherPlaceBack_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !_choosingWeatherCountry)
        {
            _ = SearchWeatherCitiesAsync(false);
            e.Handled = true;
        }
    }

    void RefreshWeatherPlaceSearch()
    {
        _weatherSearch?.Cancel();
        _weatherSearch = null;
        WeatherPlaceResults.Children.Clear();
        WeatherPlaceScroll.ScrollToTop();
        WeatherCountryText.Text = _weatherCountryName.Length > 0 ? _weatherCountryName : "Выбрать";
        if (!_choosingWeatherCountry)
        {
            _ = SearchWeatherCitiesAsync(true);
            return;
        }

        WeatherPlaceHint.Text = "Выберите страну или введите её название.";
        string query = WeatherPlaceSearch.Text.Trim();
        foreach (var country in WeatherPlaces.Countries.Where(c =>
            c.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || c.Code.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            AddWeatherPlaceOption(country.Name, country.Code, () =>
            {
                _weatherCountryCode = country.Code;
                _weatherCountryName = country.Name;
                _choosingWeatherCountry = false;
                SetWeatherSearch("");
                RefreshWeatherPlaceSearch();
                WeatherPlaceSearch.Focus();
            });
        }
        if (WeatherPlaceResults.Children.Count == 0) WeatherPlaceHint.Text = "Страна не найдена. Попробуйте другое название.";
    }

    async Task SearchWeatherCitiesAsync(bool debounce)
    {
        _weatherSearch?.Cancel();
        using var request = new CancellationTokenSource();
        _weatherSearch = request;
        string query = WeatherPlaceSearch.Text.Trim(), countryCode = _weatherCountryCode;
        WeatherPlaceResults.Children.Clear();
        try
        {
            if (query.Length < 2)
            {
                WeatherPlaceHint.Text = "Введите название города — минимум 2 буквы.";
                if (Settings.WeatherPlace is { } saved && saved.CountryCode == countryCode)
                    AddWeatherCity(saved);
                return;
            }
            WeatherPlaceHint.Text = "Ищу города…";
            if (debounce) await Task.Delay(350, request.Token);
            WeatherPlace[] places = await WeatherPlaces.SearchAsync(query, countryCode, request.Token);
            if (request.IsCancellationRequested || _panel != Panel.WeatherPlace) return;
            WeatherPlaceHint.Text = places.Length == 0
                ? "Город не найден. Проверьте страну и название."
                : "Выберите город. Регион поможет различить одинаковые названия.";
            foreach (WeatherPlace place in places) AddWeatherCity(place);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (request.IsCancellationRequested || _panel != Panel.WeatherPlace) return;
            App.Log(ex);
            WeatherPlaceHint.Text = "Не удалось загрузить города. Проверьте интернет и нажмите Enter для повтора.";
        }
        finally
        {
            if (ReferenceEquals(_weatherSearch, request)) _weatherSearch = null;
        }
    }

    void AddWeatherCity(WeatherPlace place) => AddWeatherPlaceOption(place.Name,
        string.IsNullOrWhiteSpace(place.Region) ? place.Country : $"{place.Region} · {place.Country}", () =>
        {
            Settings.WeatherPlace = place;
            Settings.WeatherManual = true;
            RefreshWeatherLocationSettings(true);
            ShowPanel(Panel.Settings);
            _ = _weather.RefreshAsync(true);
        });

    void AddWeatherPlaceOption(string title, string subtitle, Action choose)
    {
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis });
        words.Children.Add(new TextBlock
        {
            Text = subtitle, FontSize = 10.5, Foreground = _dim,
            Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var row = new Button
        {
            Style = (Style)FindResource("RowButton"), Height = 50,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = words,
            ToolTip = $"{title} · {subtitle}",
        };
        row.Click += (_, _) => choose();
        WeatherPlaceResults.Children.Add(row);
    }
}
