# Gal セリフ収集ツール

[English](README.md) | [中文](README.zh.md)

![Platform](https://img.shields.io/badge/platform-Windows-lightgrey)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)

ギャルゲームの名セリフをワンキーで収集。スクリーンショット + OCR 自動認識 + タグ整理 + 回想スライドショー。

![メインウィンドウ](images/main.png)

## 機能

| 機能 | 説明 |
|---|---|
| **ワンキー収集** | グローバルホットキー `Ctrl+Win+Z`（カスタマイズ可）、スクショ → OCR → 自動保存；OCR は Windows 標準 / Ollama ローカル / RapidOCR から選択可 |
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
| `Gal-quote-tool.exe` | ~28MB | 単一ファイル、.NET 8 ランタイムが必要 |
| `Gal-quote-tool_selfcontained.exe` | ~190MB | 単一ファイル、ランタイム不要 |
| `Gal-quote-tool_Setup.exe` | ~55MB | Inno Setup インストーラ（自己完結、アンインストーラ付き） |
| `publish-folder.zip` | ~77MB | 自己完結 ZIP、解凍して実行 |

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
- **Windows.Media.Ocr** — ネイティブ OCR（Ollama / RapidOCR に切替可）
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

## 更新履歴

### v1.2.1（2026-08-13）
- **追加** RapidOCR オフライン OCR エンジン（設定 → OCR エンジン。`rapidocr-onnxruntime` 入りの Python が必要）
- **修正** SQLite のマルチスレッド競合（バックグラウンド読込とホットキー収集の同時実行時）
- **修正** ホットキー表示の誤り（例：F5 が "t" と表示）
- **修正** 収集ホットキーと追加撮影ホットキーが重複しても警告がなかった
- **修正** バンドル ZIP インポートのパストラバーサル、設定・使用記録の無断上書き（確認を求めるように）
- **修正** フルスクリーン撮影がプライマリモニタのみ・セカンダリモニタの負座標対応
- **修正** ウィンドウモードのゲームを強制的に全画面撮影（モニタの 90% 以上を覆う場合のみ全画面に）
- **修正** 大量のセリフでタグ/グループ絞り込みが遅い（逐次クエリを排除）
- **修正** 一括削除でスクリーンショットの扱いを確認するように（単一削除と統一）
- **修正** 収集失敗時・ウィンドウ未検出時にメインウィンドウが最小化されたまま
- **修正** 収集遅延 0ms で自ウィンドウを撮影し得る問題（最低 100ms に強制）
- **改善** 起動ログ・OCR キャッシュの上限設定、バージョンをアセンブリから一元取得
