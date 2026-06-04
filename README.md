# ETWWPFApp

## 📖 專案簡介
ETWWPFApp 是一個基於 WPF 的學習範例，展示如何透過 **Event Tracing for Windows (ETW)** 來收集並顯示系統事件。  
此專案支援 **Live Session** 與 **ETL 檔案讀取**，並能顯示行程、執行緒與模組載入等事件。

目標框架：`.NET 10.0.102`  
主要用途：學習 ETW 消費與 WPF UI 整合。

---

## ✨ 功能特色
- **Live Session**：即時監控系統事件（需以管理員身份執行）。
- **ETL 檔案讀取**：解析 PerfView 或其他工具產生的 `.etl` 檔案。
- **事件顯示**：
  - ProcessStart/Stop（PID + ProcessName）
  - ThreadStart/Stop（PID + TID）
  - ImageLoad（模組名稱 + Base Address）
- **UI 效能優化策略**（詳見下方）。

---

## 🚀 使用方式
1. **Live 模式**  
   - 以管理員身份執行程式。  
   - 選擇「Live」模式並點擊「Start Live」。  
   - 程式會即時顯示行程、執行緒與模組載入事件。  

2. **ETL 模式**  
   - 選擇「From ETL」模式並點擊「Open ETL」。  
   - 選擇 PerfView 產生的 `.etl` 檔案。  
   - 程式會解析檔案並顯示事件。   

---

## ⚖️ 效能優化策略
由於 ETW 事件量龐大，特別是 ThreadStart/Stop 與 ImageLoad，容易造成 UI 卡頓。此專案採用以下策略：

- **事件過濾**：使用者可輸入 Target PID，只顯示特定行程事件。  
- **顯示數量限制**：只保留最近 N 筆事件，避免 ListBox 膨脹。  
- **分工架構**：Demo 版保持簡單，Analyzer 版展示更多事件（Thread、ImageLoad）。  
- **延遲說明**：ETW 本身事件量龐大，0.5～2 秒延遲屬正常範圍，超過 3 秒需檢查 UI 或 buffer。  

---

## 🔧 技術細節
- 使用套件：`Microsoft.Diagnostics.Tracing.TraceEvent`  
- UI 技術：WPF + Dispatcher + ConcurrentQueue  
- 支援模式：Live Session / ETL 檔案解析  
- 目標框架：`.NET 10.0.102`

---

## 📌 未來改進方向
- Target PID 過濾**：使用者可輸入 PID，只顯示特定行程的事件。
- 增加更多 ETW provider（例如 CLR GC、CPU sample）。  
- 將事件寫入檔案，提供分析報告。  
- UI 優化（虛擬化、搜尋、過濾）。  
