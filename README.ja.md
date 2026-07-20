# Gal セリフ収集ツール

[English](README.md) | [中文](README.zh.md)

![Platform](https://img.shields.io/badge/platform-Windows-lightgrey)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)

ギャルゲームの名セリフをワンキーで収集。スクリーンショット + OCR 自動認識 + タグ整理 + 回想スライドショー。

![メインウィンドウ](images/main.png)

## 機能

| 機能 | 説明 |
|---|---|
| **ワンキー収集** | グローバルホットキー `Ctrl+Win+Z`（カスタマイズ可）、スクショ → OCR → 自動保存 |
| **ゲーム名認識** | エンジン/日付の接尾辞を自動除去、カスタムルール対応 |
| **タグ** | セリフにタグ付け、フィルタリング |
| **グループ** | コレクション作成、1つのセリフが複数グループに所属可 |
| **回想スライド** | フルスクリーンモード、キーボード操作、ランダムジャンプ、最前面表示 |
| **エクスポート** | Markdown / JSON、全部またはグループ単位 |
| **インポート** | エクスポートした Markdown / JSON を取込 |
| **統計** | ゲーム別 / タグ別 / グループ別 / 月別・年別 |
| **自動起動** | Windows 起動時に自動起動、トレイに最小化 |
| **収集遅延** | 0〜2000ms で調整可 |

## ダウンロード

| ファイル | サイズ | 説明 |
|---|---|---|
| `GalQuoteCollector.exe` | ~27MB | .NET 8 ランタイムが必要 |
| `GalQuoteCollector_selfcontained.exe` | ~181MB | 単一ファイル、ランタイム不要 |
| `publish-folder.zip` | ~74MB | 自己完結 ZIP、解凍して実行 |

> データ保存先：`%LOCALAPPDATA%\GalQuoteCollector\`

## 使い方

1. exe を実行。タスクトレイに最小化されます。
2. ギャルゲームを開き、`Ctrl+Win+Z` で収集。
3. 右上に通知が表示されます（クリックでメインウィンドウを開く）。

### キーバインド

| キー | スライド / フルスクリーン中 |
|---|---|
| `Ctrl+Win+Z` | 収集（設定で変更可） |
| `←` / `→` | 前へ / 次へ |
| `Space` | 次へ |
| `Enter` | ランダムジャンプ |
| `F11` | フルスクリーン切替 |
| `F2` | 最前面切替 |
| `Esc` | フルスクリーン終了 / 閉じる |

### グループ
- 詳細パネルでグループ名を入力 → 作成
- グレーのタグをクリック → グループに参加（緑色に）
- 緑のタグをクリック → グループから脱退
- 右クリック → グループ削除
- ツールバーでグループ選択 → フィルタリング

### ゲーム名ルール
設定画面でカスタムルールを追加。例：`Summer Pockets → Summer Pockets REFLECTION BLUE` と設定すると、タイトルに "Summer Pockets" を含むウィンドウはすべてそのゲーム名に自動マッピングされます。

## 開発

### 環境
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10+

### ビルド
```bash
cd GalQuoteCollector
dotnet build
dotnet run
```

### パブリッシュ
```bash
# フレームワーク依存（小）
dotnet publish -r win-x64 -c Release --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish

# 自己完結（ポータブル）
dotnet publish -r win-x64 -c Release --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
```

## 技術スタック
- **.NET 8 + WPF** — デスクトップフレームワーク
- **Windows.Media.Ocr** — ネイティブ OCR
- **SQLite** — ローカルデータベース
- **CommunityToolkit.Mvvm** — MVVM アーキテクチャ
- **Hardcodet.NotifyIcon.Wpf** — システムトレイ
- **System.Drawing.Common** — スクリーンショット & 画像処理

## データ保存先
```
%LOCALAPPDATA%\GalQuoteCollector\
├── quotes.db          - SQLite データベース
├── settings.json      - 設定ファイル
└── screenshots\       - PNG スクリーンショット
```
