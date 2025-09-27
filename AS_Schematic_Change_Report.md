# AS 原理圖變更與流程報告 (2025-09-24)

> 截圖（目前狀態）
>
> ![AS_Current](./docs/images/AS_Current_2025-09-24.png)
> （此為佔位：請將你提供的最新截圖另存為 `docs/images/AS_Current_2025-09-24.png` 放入，以便文件引用。）

---
## 1. 緣起與三個階段對照

| 階段 | 畫面表現 | 主要程式策略 | 問題/結果 |
|------|----------|--------------|-----------|
| A. 初版 (有資料但不符合) | 有許多黑點 + 多條線 | `ProjectAllSystemPortsToData`：以選到的第一段管件所在系統，收集整個系統所有 DistributionPort 並投影；邊由 `IfcRelConnectsPorts` 建立 | 與規格「只取兩段管的四個 Port、兩條線」不符，資訊過多、方向未對齊 Canvas Y-down |
| B. 中間版 (無資料/空白) | Canvas 顯示 0 點 0 線 | 嘗試改為僅取兩段管的 Ports，但：1) 某些模型段本身沒有 `HasPorts` ；2) 退化平面/無 Port 時沒有後援；3) 早期 patch 破壞方法結構導致投影流程在錯誤分支提前 return | 遇到沒有直接 Port 的段時完全無節點，畫面空白，使用者誤以為程式失效 |
| C. 現行版 (四點/兩線或救援點) | 預期 ≤ 4 點，2 線；若無原生 Port 使用後援/救援 | `ProjectSelectedSegmentsPortsToData`：僅收集兩段管各 2 個 Port；多層後援(相連元件 Ports → 全域最近 Ports → 最終救援全域挑點並連線)；V 軸倒置 | 符合規格；極端缺 Port 案例仍保證不空白；可進一步調整方向或顯示標籤 |

### 1.1 由「有資料」到「無資料」的原因
1. 目標從「整個系統」縮到「僅兩段管」→ 原本大量節點/邊的來源被移除。  
2. PipeSegment 在該模型中沒有 `HasPorts`，導致 `GetPorts(seg).Take(2)` 得到空集合。  
3. 缺少後援機制（未去相鄰元件或全局 Ports 採樣），節點集合為 0。  
4. 早期 refactor 時把新程式片段意外插進 `ProjectAllSystemPortsToData` 中間造成錯誤 return/中斷，投影計算未完整執行。  

### 1.2 由「無資料」到「現行可顯示」的修復策略
- 重寫 `GeneratePortPointSchematicFromSegmentsAsync` → 單一責任：計算平面與座標系再委派到 *Two-Segment Only* 方法。  
- 新增 `ProjectSelectedSegmentsPortsToData`：專注於 4 點 2 線的最小拓撲。  
- 加入後援層級：
  1) 原生段 Ports  
  2) `IfcRelConnectsElements` 相連元件的 Ports（距離最近排序）  
  3) 全模型最近 Ports（距離段中心）  
  4) 最終救援：若還是 0，從全域最近集合建點並用「段中心最近兩點」推導線（標記 `IsInferred=true`）  
- 對齊 Canvas：v 軸取反，避免上下顛倒。  
- 清理殘碼、避免中途 return。  

---
## 2. 現行程式產生 Canvas 可視圖流程

下列描述僅針對 AS 原理圖（不影響一般系統拓撲視圖）：

1. 使用者於 UI 選擇兩段 `IfcPipeSegment` (seg1, seg2)。
2. 呼叫 `GeneratePortPointSchematicFromSegmentsAsync(ifcModel, seg1, seg2)`：
   - 2.1 收集兩段各最多 2 個 Port 的 3D 座標 (若無則暫存為空)。
   - 2.2 若收集到 ≥3 個點：用前三點建立平面；不足 3 點 → 用兩段中心點 + 人工偏移補點確保不退化。
   - 2.3 建立局部座標：`u = normalize(v1)`，`v = normalize(cross(n,u))`，再令 `vCanvas = -v`（Canvas Y-down）。
   - 2.4 呼叫 `ProjectSelectedSegmentsPortsToData(...)`。 
3. 在 `ProjectSelectedSegmentsPortsToData`：
   - 3.1 `addSegmentPorts(seg)`：
       - 取段自身 Ports（最多 2）。
       - 若 <2：用 `GetNearestPortsFromConnectedElements`（透過 `IfcRelConnectsElements` 找鄰居元件的 Ports，按距離排序取 2）。
       - 若仍 <2：用 `GetNearestPortsGlobal`（全模型 Ports 與段中心距離最小者）。
       - 投影到 (u, vCanvas) 平面 → 產生 `SchematicNode`（黑點）。
   - 3.2 對 seg1、seg2 各自呼叫一次 `addSegmentPorts`。
   - 3.3 `connectTwoPorts(seg)`：再取段的前兩個（或後援結果）節點連成一條 `SchematicEdge`（黑線）。
   - 3.4 如果節點仍為 0（極端情況）：
       - 用兩段中心點各自尋找全局最近 Ports 建點（最多 4）。
       - 以段中心為參考挑最近兩點建立一條 inferred 線（`IsInferred=true`）。
4. 回傳 `SchematicData (Nodes, Edges)` 給 ViewModel。
5. ViewModel 套用 fit-to-canvas：計算 2D 節點 bounding box → scale & translate 到 800x600（含 padding 20）。
6. View (XAML `ItemsControl`) 繪出：
   - Nodes：黑色 6px 圓點 (Canvas.Left/Top = node.Position2D)。
   - Edges：Line（1.5px 黑色）綁定起迄節點 Position2D。  
7. 使用者互動：滑鼠滾輪縮放、拖移平移、按鈕重置或重新布局（重新執行 fit-to-canvas）。

流程圖（概念）：
```
選兩段管件
    ↓
收集≥3點? ── 否 ──> 中心點+補點建平面
    │是
    ↓
建立 u,vCanvas 座標系
    ↓
ProjectSelectedSegmentsPortsToData
    ↓  addSegmentPorts (Ports→相連元件→全域)
  得到節點 ≤4
    ↓  connectTwoPorts (各段 0~1 條線)
節點=0? → 最終救援 (全域最近 Ports + inferred 線)
    ↓
SchematicData → Fit → 顯示
```

---
## 3. 目前程式碼關鍵方法列表

| 方法 | 功能摘要 |
|------|----------|
| `GeneratePortPointSchematicFromSegmentsAsync` | AS 流程入口：建立參考平面 + 呼叫精簡投影方法 |
| `ProjectSelectedSegmentsPortsToData` | 只處理兩段管件 Ports → 投影 → 連線（含多層後援 + 救援） |
| `GetNearestPortsFromConnectedElements` | 透過 `IfcRelConnectsElements` 尋找與段相連元件 Ports |
| `GetNearestPortsGlobal` | 全模型範圍尋找最近 Ports（距離阈值排序） |
| `GetPorts` | 從元素（若為配電元素）取得 HasPorts 中的 Ports |
| `ProjectToPlane` | 3D → 2D 平面坐標投影 |

---
## 4. 目前畫面（示意）

上圖顯示：節點 4、邊 2 計數（標題列），但視窗內只見 1 個點 → 代表其餘節點可能疊得很近或在可視區外尚未經 fit（若剛縮放/移動過）。建議：
1. 點擊「重置視圖」按鈕觸發重新 fit。  
2. 暫時加上節點座標/名稱文字（可後續小改進）。  

---
## 5. 驗收檢查表 (Checklist)

| 項目 | 狀態 | 備註 |
|------|------|------|
| 僅使用兩段管件 | ✅ | 僅 seg1, seg2 影響結果 |
| 最多 4 個節點 | ✅ | 保證不超過 4；救援亦 ≤4 |
| 兩條線 (各段一條) | ✅ | 若段合法取到 ≥2 端點 |
| 無 Port 時不空白 | ✅ | 多層後援 + 最終救援 |
| Canvas Y 軸向下對齊 | ✅ | v 軸取反 |
| Fit-to-canvas 正常 | ⏳ | 若視覺上只見 1 點需再檢查布局流程 |

---
## 6. 後續建議
1. 加入「顯示 Port 標籤 / 座標」切換，便於檢查重疊或重合。  
2. 加入「交換 / 反轉 U 軸」按鈕，快速調整視覺朝向。  
3. 在 `IsInferred=true` 的 Edge 上用虛線或淡色提示來源是救援線。  
4. Fit 演算法可在節點 BoundingBox 面積極小時自動放大最小顯示尺度（避免四點重疊看不見）。  
5. 為後援/救援層級加上簡易圖例或 log 面板。  

---
## 7. 變更摘要 (Changelog 摘要)
- 新增：`ProjectSelectedSegmentsPortsToData`、後援與救援邏輯。  
- 重寫：`GeneratePortPointSchematicFromSegmentsAsync` 只負責平面計算與委派。  
- 修復：Patch 中斷/混入舊方法的語法損壞問題。  
- 調整：V 軸倒置以符合 Canvas。  
- 新增：距離排序挑選最近 Ports。  

---
## 8. 可能的剩餘風險
| 風險 | 描述 | 緩解 |
|------|------|------|
| 極端模型無任何 DistributionPort | 救援仍可能挑不到點 | 可再造假節點 (segment center endpoints) |
| 節點重疊 | 四個投影點幾何上非常接近 | 增加最小分離距離或 jitter |
| 畫面只見 1 點 | 全部點重合；fit 將它們視為單一 bbox | 提供疊加標籤 / jitter / 最小縮放 |

---
## 9. 附錄：典型日誌前綴
| 前綴 | 說明 |
|------|------|
| `[Service][AS-min]` | 正常兩段簡化流程主要訊息 |
| `[Service][AS-min] Fallback` | 使用相連元件或全域 Ports 後援 |
| `[Service][AS-min][RESCUE]` | 進入最終救援（原生、相連、全域都無法滿足） |

---
## 10. V1 多系統手動平面 Ports 點雲（2025-09-24）

> 此章描述「AS 原理圖 V1」與「兩段最小拓撲 (AS-min)」之差異，著重於資料診斷與來源透明化。

### 10.1 動機
- 兩段最小拓撲僅顯示參考段的 4 點 2 線，無法全面檢視整個系統 Ports 分佈與 IFC 資料完整度。
- 部分模型 Ports 不在 HasPorts 而是透過 RelNests 階層嵌套，導致傳統抽取方式顯示為空（誤判為無資料）。
- 需要一個「不丟資料」的點雲模式協助：
    1) 驗證系統 Ports 來源分佈（HasPorts / Nested / Fallback）
    2) 分辨缺失宿主或分類失敗的 Ports（顏色提示）
    3) 後續匯出與審查（PortDetail）

### 10.2 與 AS-min 的比較
| 面向 | AS-min (兩段) | V1 (多系統點雲) |
|------|---------------|------------------|
| 節點數 | ≤ 4 | 系統所有 Ports（無硬上限） |
| 邊 | 每段 0~1 條，共 ≤2 | 不繪製（集中於點與來源） |
| 平面 | 自動計算（兩段端點/中心） | 使用者手動選擇 XY/XZ/YZ |
| 後援機制 | 兩段 → 相連 → 全域 → 救援 | Ports 來源三層（HasPorts / Nested / 全域掃描） |
| 著色 | 全部黑點（早期） | 黑 = PipeSegment 端點；紅 = 其他宿主 / Fallback |
| Log | 簡單統計 | 詳細逐 Port (PortDetail) |
| 使用目的 | 視覺化最小拓撲 | 資料品質檢查、來源追溯 |

### 10.3 三層 Port 抽取策略
| 層級 | 邏輯 | 說明 | 計數欄位 |
|------|------|------|----------|
| 1 | HasPorts | 成員元素直接擁有的 Ports | viaHasPorts |
| 2 | RelNests | Port 以嵌套方式附著於元素 | viaNested |
| 3 | Global Fallback | 全模型 DistributionPort 掃描 + 過濾/近似 | viaFallback |

> 若 viaHasPorts + viaNested = 0，通常代表模型採「嵌套以外的異常結構」或系統成員關聯缺失。

### 10.4 PortDetail 結構
```
Label, Name, HostLabel, HostIfcType, Source, RawXYZ, Projected(x,y), HostName, IsPipeSegment
```
用途：
- 日誌檢視（即時）
- 後續匯出（CSV/JSON，規劃中）
- 快速定位資料異常（大量 Fallback 即警訊）

### 10.5 著色與 Tooltip
- 黑點：Host 為 IfcPipeSegment → 判定為管段端口（期望主幹）
- 紅點：其它型別（Valve/Fitting/Terminal/Fallback/Host 未解）
- Tooltip：Port 名稱、Label、Host IfcType、Host Label、IsPipeSegment。

### 10.6 「全部紅點」缺陷修復
| 項目 | 說明 |
|------|------|
| 症狀 | 所有點呈紅色，顏色規則未生效 |
| 根因 | 渲染順序與以 PortLabel Key 映射的 meta 不一致，導致錯配 |
| 修復 | `LoadPointsAsync` 以 index 對齊 meta；生成 meta 時保持 push 順序一致 |
| 結果 | 黑/紅分類恢復正確，可辨識 PipeSegment 端點 |

### 10.7 已知限制
1. 宿主解析尚未反向追 RelNests 上層鏈（導致部分 Pipe 端口標紅）。  
2. 疊點未顯示重合數；重合時肉眼容易忽略。  
3. 無圖例 / 無過濾切換（僅管段端點 vs 全部）。  
4. 平面切換需重新執行流程（尚無快取）。  

### 10.8 後續 Roadmap（與主 Schematic 報告同步）
| 項目 | 目標 | 優先級 |
|------|------|--------|
| RelNests 反向宿主解析 | 降低誤標紅點 | 高 |
| PortDetail 匯出 | CSV / JSON 批次分析 | 高 |
| 圖例 / 過濾 UI | 提升可讀性 | 中 |
| 平面快速切換 | 不重新查 IFC，再投影 | 中 |
| 疊點標示 / jitter | 改善重疊辨識 | 中 |
| Host 型別多色 | 細化分類（Valve/Terminal...） | 低 |

### 10.9 驗收檢查（V1）
| 項目 | 指標 | 通過條件 |
|------|------|-----------|
| 多系統支持 | 使用者一次選多系統 | 為每系統建立獨立點集合與統計 |
| 三層計數 | viaHasPorts/viaNested/viaFallback | 均顯示且總和 = 顯示點數 |
| 顏色規則 | 黑/紅明確 | 同一系統至少出現兩種顏色（若資料允許） |
| PortDetail | 日誌逐列輸出 | 每點包含 Label/Source/Host 型別 |
| 無邊線 | 僅點 | 視圖無 Edge 元件 |

---
(完)
