# AS 原理圖 V1 詳細運作報告 (2025-09-24)

> 目的：完整說明 *AS 原理圖 V1*（多系統手動平面 Ports 點雲模式）之資料來源、流程演算法、投影數學、診斷與未來擴充方向；協助開發 / 測試 / 產品驗收人員快速理解與追蹤。

---
## 目錄
1. 核心定位 (What & Why)
2. 高階流程總覽
3. 使用情境與互動腳本
4. 系統選取與平面選擇機制
5. Port 抽取三層策略 (Data Acquisition Layers)
6. PortDetail 結構與語意
7. 投影數學 (Plane & Coordinate Mapping)
8. 顏色分類與 Tooltip 規則
9. 日誌輸出與統計指標
10. 與 AS-min（兩段最小拓撲）差異矩陣
11. 資料品質診斷場景解析
12. 效能與複雜度分析
13. 風險與限制
14. 已修復問題 (Bug Log)
15. 未來 Roadmap 與優先順序
16. ASCII 流程圖與狀態機摘要
17. 驗收檢查表 (Acceptance Checklist)
18. 名詞對照 (Glossary)

---
## 1. 核心定位 (What & Why)
**AS 原理圖 V1** 是一個「不丟資料」的 *港口診斷視圖*：
- What：將一個或多個 IfcSystem 的所有 Distribution Port 投影成 2D 點雲；不畫連線，專注於資料完整度、來源層級統計與宿主分類。
- Why：早期兩段最小拓撲 (AS-min) 無法揭露系統層級 Ports 缺失與來源不均；V1 提供高透明度輸出，協助釐清 IFC 模型是否：
  - Ports 存於 HasPorts 或僅 Nested
  - 存在大量孤立 Ports（Fallback）
  - 管段端點 (PipeSegment) 缺失比例過高

---
## 2. 高階流程總覽
1. 使用者觸發「AS V1」指令
2. 彈出系統選擇（支援多選）
3. 彈出平面選擇 (XY/XZ/YZ)
4. 對每個系統：
   - 依序執行三層 Port 抽取策略
   - 建立 PortDetail 清單 + viaHasPorts / viaNested / viaFallback 計數
   - 主機型別判斷（PipeSegment → 黑 / 其他 → 紅）
   - 投影至所選平面 → 正規化 / Fit-to-Canvas
5. UI：渲染點集合 + 逐行日誌輸出
6. 使用者可在視圖進行平移/縮放與再次生成

---
## 3. 使用情境與互動腳本
| 情境 | 步驟 | 預期結果 |
|------|------|----------|
| 快速驗證系統 Ports 來源 | 選 1 個系統 → 選 XY → 查看統計 | viaHasPorts > 0；若 =0 且 viaNested>0 表示模型使用嵌套策略 |
| 檢查管段端點完整性 | 選多系統 → 選 XZ | 黑點比例顯示主幹端口覆蓋；過低需追 IFC 品質 |
| 找資料異常 | 任意系統 → 選平面 | viaFallback 占比高代表系統關聯不足或宿主缺失 |

---
## 4. 系統選取與平面選擇機制
- 系統來源：遍歷 `IIfcSystem` / `IIfcDistributionSystem`；以 `IfcRelAssignsToGroup` 成員绑定。
- 多選：逐系統串行處理，避免 UI 阻塞；可平行但暫緩以利除錯。
- 平面選擇：
  - XY: (X,Y)
  - XZ: (X,Z)
  - YZ: (Y,Z)
- 後續可擴充：自動建議（採跨度最大二軸）+ 快速切換无需重新查 IFC（快取 RawXYZ）。

---
## 5. Port 抽取三層策略 (Data Acquisition Layers)
| 層級 | 名稱 | 邏輯 | 典型 IFC 結構 | 失敗後進下一層觸發 | 計數欄位 |
|------|------|------|---------------|----------------------|----------|
| 1 | HasPorts | 直接從成員元素的 `HasPorts` 集合擷取 | 標準機電模型 | 元素無 Ports / 集合空 | viaHasPorts |
| 2 | Nested | 利用 `IfcRelNests` 找被元素「包含」的 Ports | 模型把 Ports 視為子構件 | 沒有 Nests 或全空 | viaNested |
| 3 | Fallback (Global) | 全模型掃描 `IfcDistributionPort` → 過濾/近似系統成員 | 系統關聯缺失 / 資料破碎 | 不再下探 | viaFallback |

決策順序：第一個非空層級之 Ports 仍會與後續層級合併嗎？→ 會；因為目標是 *全集合* 與來源分類，而非短路返回。

---
## 6. PortDetail 結構與語意
```
PortDetail {
  int    Label;           // Port EntityLabel
  string Name;            // 友好名稱（含空字串）
  int?   HostLabel;       // 宿主元素 EntityLabel（可能缺）
  string? HostIfcType;    // 宿主 Ifc 類型（判斷顏色）
  string Source;          // HasPorts | Nested | Fallback
  (double X,double Y,double Z) Raw; // 原始 3D 座標
  (double x,double y) Projected;    // 投影後 2D
  string? HostName;       // 宿主名稱
  bool IsPipeSegment;     // 是否宿主為 IfcPipeSegment
}
```
語意重點：
- `Source` 只標示第一個發現來源層級；若同一 Port 在 HasPorts 與 Nested 同時可達，不重複列；HasPorts 優先。
- `IsPipeSegment` 決定顏色（黑/紅）。
- `HostLabel` 缺失時（Fallback 常見）→ 多為模型系統關聯問題。

---
## 7. 投影數學 (Plane & Coordinate Mapping)
### 7.1 座標選軸
給定原始 Port 位置 P = (X,Y,Z)；選擇平面：
- XY: 2D = (X, Y)
- XZ: 2D = (X, Z)
- YZ: 2D = (Y, Z)

### 7.2 Fit-to-Canvas（簡化）
1. 找所有投影點的 bbox：minX, maxX, minY, maxY
2. 計算 spanX = maxX-minX，spanY = maxY-minY
3. 以 `scale = min( (CanvasW-2*pad)/spanX, (CanvasH-2*pad)/spanY )`
4. 每點：( (x-minX)*scale + pad, (y-minY)*scale + pad )
5. 無跨度（全部重合）時：
   - 指派預設微距離 jitter 或維持單一點中心顯示（後續增強）

### 7.3 後續規劃
- 支援自動平面建議（跨度最小剔除法）
- 疊點 jitter / 密度標示 (Kernel Density / QuadTree)

---
## 8. 顏色分類與 Tooltip 規則
| 條件 | 顏色 | 說明 |
|------|------|------|
| `IsPipeSegment == true` | 黑 (#000000) | 視為管段端口（主幹） |
| 其他 | 紅 (#C62828) | 其他設備 / Fallback / 未解析宿主 |

Tooltip 欄位：`Port Name / Label / HostIfcType / HostLabel / IsPipeSegment`。

---
## 9. 日誌輸出與統計指標
| 指標 | 說明 | 用途 |
|------|------|------|
| viaHasPorts | 直接 HasPorts 取得的 Port 數 | IFC 標準性檢查 |
| viaNested | 嵌套抽取 (RelNests) Port 數 | 模型是否採用階層式 Ports | 
| viaFallback | 全域掃描遺失關聯 Port 數 | 資料缺失量化 |
| totalPorts | 三層合併後唯一 Port 數 | 系統規模衡量 |
| pipeSegmentRatio | 黑點 / total | 管段端點覆蓋度 |

日誌範例（簡化）：
```
[Sys=A_001] viaHasPorts=24 viaNested=12 viaFallback=3 total=39 pipeSegmentRatio=0.56
[Port] L=3412 Name=P-In Host=PipeSeg(889) Source=HasPorts Proj=(123.4,45.6)
[Port] L=3420 Name=J1    Host=Valve(901)  Source=Nested  Proj=(130.1,44.9)
[Port] L=3550 Name=?     Host=?           Source=Fallback Proj=(140.2,50.0)
```

---
## 10. 與 AS-min 差異矩陣
| 面向 | AS-min | V1 |
|------|--------|----|
| 節點數 | ≤4 | 全部 Ports |
| 邊 | 0~2 | 無 |
| 平面 | 自動推導 | 手動選擇 |
| 後援 | 兩段專屬多層 | Port 來源三層 (Has/Nested/Fallback) |
| 顏色 | 單色 (早期) | 黑/紅分類 |
| 日誌 | 基本統計 | Per-Port Detail |
| 目的 | 圖形最小拓撲 | 資料品質審視 |

---
## 11. 資料品質診斷場景解析
| 症狀 | 觀察 | 解讀 | 建議行動 |
|------|------|------|----------|
| viaHasPorts=0, viaNested>0 | 僅第二層有值 | 模型使用 Nests，不是標準 HasPorts | 確認建模規範；保留分類欄位 |
| viaHasPorts=0, viaNested=0, viaFallback>>0 | 全靠 fallback | 系統關聯缺或 Ports 漏掛 | 追 IFC RelAssignsToGroup / RelNests 完整性 |
| pipeSegmentRatio 遠低 | 多為紅點 | 管段缺 Ports 或宿主解析失敗 | 實作 RelNests 反向解析 / 補 IFC | 
| 同一座標大量重疊 | bbox 幾乎為 0 | 幾何定位缺失或全部投影到一點 | 加 jitter 或檢查 LocalPlacement |

---
## 12. 效能與複雜度分析
| 階段 | 複雜度 | 瓶頸來源 | 現況 | 改善構想 |
|------|--------|----------|------|----------|
| 系統成員收集 | O(S + M) | 系統數 S, 成員 M | 輕 | 快取系統→成員 map |
| HasPorts 抽取 | O(P1) | Ports 數 | 輕 | 直接集合迭代 |
| RelNests 抽取 | O(Rn) | Nests 關係 | 中 | 可索引 parent→children |
| 全域掃描 | O(Pall) | 模型全部 Ports | 中 | 預建全域 Port 索引一次重用 |
| 投影 & Fit | O(N) | N = totalPorts | 輕 | bbox & 線性轉換 |

> 目前無需特別平行；當 totalPorts > 50k 再評估 partition。

---
## 13. 風險與限制
| 類型 | 描述 | 當前處置 | 後續緩解 |
|------|------|----------|----------|
| 資料 | 宿主解析缺失（Fallback 過多） | 標記紅點 + Source=Fallback | RelNests 反向追溯 |
| 視覺 | 重疊點不可辨識 | 使用者可縮放/平移 | jitter + 疊點計數 |
| 使用 | 手動平面需嘗試 | 默認提示三選 | 快速平面切換 + 自動建議 |
| 效能 | 大型模型 Ports 遍歷 | 本地測試中等規模 OK | Port 索引 + 延遲載入 |
| 一致性 | 多系統間顏色語意 | 全域黑/紅二元 | 擴展多色映射 |

---
## 14. 已修復問題 (Bug Log)
| 日期 | 標題 | 描述 | 修復要點 |
|------|------|------|----------|
| 2025-09-24 | 全部紅點 | `IsPipeSegment` 判斷失效（meta 與渲染順序不一致） | Index 對齊 metaList；移除 label-based dict |

---
## 15. 未來 Roadmap 與優先順序
| 優先 | 項目 | 描述 | 預期收益 |
|------|------|------|----------|
| 高 | RelNests 反向宿主解析 | 找出真正宿主，降低誤標 | 黑點比例準確度提升 |
| 高 | PortDetail 匯出 | CSV/JSON 一鍵輸出 | 報告/外部分析流程 | 
| 中 | 圖例 / 過濾 | 可視化解釋 + 篩選管段端點 | 使用者理解加速 |
| 中 | 平面快切 | 不重查 IFC，再投影 | 操作效率 |
| 中 | 疊點處理 | jitter + 重合計數 | 探測資料異常 | 
| 低 | 多色主題 | Valve/Fitting/Terminal 個別色 | 精細診斷 |

---
## 16. ASCII 流程圖與狀態機摘要
```
+-----------------+      +------------------+      +------------------+
| 使用者啟動 V1   | ---> | 選擇系統(多選)    | ---> | 選擇平面(XY/XZ/YZ) |
+-----------------+      +------------------+      +------------------+
            |                                      |
            v                                      v
      For Each System                       建立平面映射
            |                                      |
            v                                      v
   收集 Ports 三層 (HasPorts -> Nested -> Global Fallback)
            |
            v
      標註來源 Source / 判斷宿主 IsPipeSegment
            |
            v
        投影 & Fit-to-Canvas
            |
            v
        輸出 PortDetail 日誌
            |
            v
        渲染點 (黑/紅)
            |
            v
         等待使用者互動 (縮放/平移/重新生成)
```
狀態機（簡化）：Idle → SelectingSystems → SelectingPlane → Processing(system迴圈) → Rendering → Idle。

---
## 17. 驗收檢查表 (Acceptance Checklist)
| 項目 | 驗收動作 | 期望 | 結果 |
|------|----------|------|------|
| 平面選擇 | 選 XY | 點雲生成 |  |
| 多系統 | 選 2+ 系統 | 日誌分段列出 |  |
| 三層統計 | 檢視日誌第一行 | 三計數 + total |  |
| 顏色分類 | 視覺觀察 | 黑/紅並存（若資料允許） |  |
| PortDetail | 滑動日誌 | 每點一行 |  |
| Tooltip | 滑鼠懸停 | 顯示完整資訊 |  |
| Fallback 減少 | 改善 IFC 後重跑 | viaFallback 降低 |  |

---
## 18. 名詞對照 (Glossary)
| 名詞 | 說明 |
|------|------|
| HasPorts | 元素直接列出的 Ports 關聯集合 |
| RelNests | IFC 關係：一元素巢狀包含另一元素 (Nests) |
| Fallback | 在本系統關聯路徑無法取得時以全域掃描補齊 |
| PortDetail | 每個投影點的診斷記錄物件 |
| PipeSegment Ratio | 管段端點數 / 全部 Ports 數 |
| 投影平面 | 使用者選擇映射的 2D 平面 (XY/XZ/YZ) |
| BBox | Bounding Box；點集合最小外框 |

---
(完)
