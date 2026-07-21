using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZLauncher.Services;

namespace ZLauncher.ViewModels;

public partial class NewsCardViewModel : ViewModelBase
{
    private readonly MinecraftNewsService _news;
    private readonly TranslationService _translator;
    private CancellationTokenSource? _enrichCts;

    public MinecraftNewsItem Model { get; }

    public string DateLabel => Model.Date;
    public string TagLabel => Model.Tag;
    public string CategoryLabel => Model.Category;
    public bool HasImage => Image is not null;

    [ObservableProperty]
    private string _titleRu = "";

    [ObservableProperty]
    private string _textRu = "";

    [ObservableProperty]
    private double _titleOpacity = 1;

    [ObservableProperty]
    private double _textOpacity = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private Bitmap? _image;

    [ObservableProperty]
    private bool _isImageLoading = true;

    [ObservableProperty]
    private bool _isTranslating = true;

    public NewsCardViewModel(
        MinecraftNewsItem model,
        MinecraftNewsService news,
        TranslationService translator)
    {
        Model = model;
        _news = news;
        _translator = translator;
        TitleRu = model.Title;
        TextRu = string.IsNullOrWhiteSpace(model.Text) ? "…" : model.Text;
    }

    public void StartEnrich(CancellationToken parentToken)
    {
        _enrichCts?.Cancel();
        _enrichCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        var token = _enrichCts.Token;
        _ = EnrichAsync(token);
    }

    private async Task EnrichAsync(CancellationToken token)
    {
        var imgTask = LoadImageAsync(token);
        var trTask = TranslateAsync(token);
        await Task.WhenAll(imgTask, trTask).ConfigureAwait(false);
    }

    private async Task LoadImageAsync(CancellationToken token)
    {
        try
        {
            var bmp = await _news.LoadImageAsync(Model.ImageUrl, token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                bmp?.Dispose();
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested)
                {
                    bmp?.Dispose();
                    return;
                }

                Image?.Dispose();
                Image = bmp;
                IsImageLoading = false;
            });
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsImageLoading = false);
        }
    }

    private async Task TranslateAsync(CancellationToken token)
    {
        try
        {
            var titleTask = _translator.ToRussianAsync(Model.Title, token);
            var textTask = _translator.ToRussianAsync(Model.Text, token);
            await Task.WhenAll(titleTask, textTask).ConfigureAwait(false);

            if (token.IsCancellationRequested)
                return;

            var title = await titleTask.ConfigureAwait(false);
            var text = await textTask.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(title))
                title = Model.Title;
            if (string.IsNullOrWhiteSpace(text))
                text = Model.Text;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested)
                    return;
                // Лёгкий fade при смене перевода
                TitleOpacity = 0;
                TextOpacity = 0;
            });

            await Task.Delay(120, token).ConfigureAwait(true);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested)
                    return;
                TitleRu = title;
                TextRu = text;
                TitleOpacity = 1;
                TextOpacity = 1;
                IsTranslating = false;
            });
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsTranslating = false);
        }
    }

    [RelayCommand]
    private void OpenArticle()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Model.ReadMoreUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore
        }
    }

    public void DisposeResources()
    {
        _enrichCts?.Cancel();
        Image?.Dispose();
        Image = null;
    }
}
