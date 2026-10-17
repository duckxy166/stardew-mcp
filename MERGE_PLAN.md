# แผน merge PR #59–#88 (ทำต่อใน local)

ไฟล์นี้ไม่ได้ commit เข้า repo ลบทิ้งได้เมื่อใช้เสร็จ

## สถานะตอนนี้

| | PR |
|---|---|
| merge เข้า `main` แล้ว | #75 (briefing, squash), #59 (#5 fight, merge commit), #61 (#6 mine survival, squash) |
| ยังเปิดอยู่ (Ready for review) | #60 #62 #63 #64 #65 #66 #67 #68 #69 #70 #71 #72 #73 #74 #76 #77 #78 #79 #80 #81 #82 #83 #84 #85 #86 #87 #88 |

- โค้ด C# ใน PR ทั้งหมด **ยังไม่เคย compile** (sandbox ไม่มี dotnet/DLL เกม) ตรวจแค่ syntax กับ pytest ฝั่ง Python
- 22 จาก 27 PR ที่เหลือ conflict กับ `main` ปัจจุบัน ส่วนที่ไม่ conflict: #15 #16 #11 #13 #31 #17
- `main` ที่ merge ไป 3 PR แล้วยังไม่ได้ build ลองก่อน: `cd mod/StardewMCP && dotnet build -p:EnableModDeploy=false -p:EnableModZip=false`

## กติกา

1. merge ทีละ PR ตามลำดับด้านล่าง ห้ามขนานกัน เพราะทุกครั้งที่ merge PR อื่นจะ conflict ใหม่
2. ก่อน merge แต่ละ PR: ดึง `main` เข้า branch ของมัน แก้ conflict แล้ว **build ให้ผ่าน** ค่อย push และ merge
3. PR ที่เป็นฐานของ stack ใช้ **merge commit** (ไม่ squash) เพื่อให้ PR ที่ซ้อนอยู่ merge ต่อได้สะอาด PR อื่นใช้ squash
4. หลัง merge ฐานของ stack ให้เปลี่ยน base ของ PR ที่ซ้อนเป็น `main` ก่อนค่อยดึง `main` เข้า branch
5. ต่อ 1 กลุ่มเสร็จ ให้รัน `py runner/smoke.py` ของกลุ่มนั้นกับเกมจริง ถ้ามีปัญหาย้อนด้วย `git revert -m 1` ก่อนไปกลุ่มต่อไป

## ขั้นตอนต่อ 1 PR (แทน N ด้วยเลข issue ของ branch เช่น `fix/issue-21`)

```bash
git fetch origin
git switch -c merge/N origin/fix/issue-N
git merge origin/main            # ถ้า conflict ดูตารางวิธีแก้ด้านล่าง
cd mod/StardewMCP && dotnet build -p:EnableModDeploy=false -p:EnableModZip=false && cd ../..
python -m pytest -q runner/tests studio      # ใช้ได้เท่าที่มีโฟลเดอร์
git push origin merge/N:fix/issue-N          # push ขึ้น branch ของ PR (ไม่ force)
# แล้วกด merge ใน GitHub (merge commit สำหรับฐาน stack, squash สำหรับตัวอื่น)
```

ข้อความ commit และ merge ให้ลงท้ายด้วย trailer ของ session ตามที่ใช้มาตลอด (ไม่บังคับถ้าทำเอง)

## ลำดับ merge

### กลุ่ม A: stack (ฐาน → ตัวที่ซ้อน)
| ลำดับ | PR | วิธี merge | หมายเหตุ |
|---|---|---|---|
| A1 | #62 (#21 journal) | merge commit | conflict: README, AgentHttp.cs, run_days.py, smoke.py |
| A2 | #64 (#22, ซ้อน #21) | squash | เปลี่ยน base เป็น `main` ก่อน |
| A3 | #63 (#52 shared economy) | merge commit | conflict: ModEntry.cs |
| A4 | #68 (#40, ซ้อน #52) | squash | เปลี่ยน base เป็น `main` ก่อน |
| A5 | #67 (#3 craft/place/chest) | merge commit | conflict: docs/coverage.md |
| A6 | #86 (#14, ซ้อน #3) | squash | เปลี่ยน base เป็น `main` ก่อน |
| A7 | #73 (#9 villagers) | merge commit | conflict: README, AgentHttp.cs |
| A8 | #83 (#53, ซ้อน #9) | squash | เปลี่ยน base เป็น `main` ก่อน |
| A9 | #76 (#13 Community Center) | merge commit | ไม่ conflict ตอนนี้ |
| A10 | #80 (#26, ซ้อน #13) | squash | เปลี่ยน base เป็น `main` ก่อน |

### กลุ่ม B: PR เดี่ยว (squash ทั้งหมด)
`#60` (#23), `#65` (#24), `#66` (#2), `#69` (#4), `#70` (#15), `#71` (#16), `#72` (#11), `#74` (#25), `#77` (#31), `#78` (#30), `#79` (#32), `#81` (#10), `#85` (#27), `#87` (#54), `#88` (#34)

แนะนำให้ merge ตัวที่ไม่ conflict ก่อน (#70 #71 #72 #77) แล้วค่อยตัวที่เหลือ

### กลุ่ม C: ปิดท้าย
| ลำดับ | PR | หมายเหตุ |
|---|---|---|
| C1 | #82 (#17 test suite) | merge ก่อน #84 เสมอ: ย้าย `CallContext`/`ToolDef`/`Str`/`Int` ออกจาก `AgentRuntime.cs`/`AgentTools.cs` และลบ `AgentHttp.Dispatch` |
| C2 | #84 (#18 structured results) | ทำ `CallContext` เป็น partial ต้องปรับให้เข้ากับโครงใหม่ของ #82 |

## วิธีแก้ conflict ตามไฟล์

| ไฟล์ | วิธีแก้ |
|---|---|
| `README.md`, `docs/coverage.md`, `runner/system_prompt.md` | ส่วนใหญ่เพิ่มบรรทัดทั้งสองฝั่ง เก็บไว้ทั้งสองฝั่ง ยกเว้นแถวเดียวกันใน coverage ที่ต้องรวมเป็นแถวเดียว และบรรทัดเดียวกันที่ PR ปรับถ้อยคำ (เช่น `Monsters ... fight`) ให้เก็บแค่เวอร์ชันของ PR |
| `AgentHttp.cs` | ส่วนใหญ่เป็น comment รายการ endpoint ใน header เก็บทั้งสองฝั่ง |
| `runner/run_days.py` | ต้องอ่านทีละจุด PR ที่แก้พร้อมกัน: #9 #21 #22 #23 #24 #25 #29 #30 #32 #18 #34 ตัวอย่างที่เจอแล้ว (#21 ชน #29): ใช้ `prompt = briefing.morning_prompt(day, briefing.build(api_get, day, "p1", out_dir=out_dir))` แล้วต่อด้วย `prompt = memory.with_reminder(prompt, state, log_dir)` และ import ทั้ง `memory` กับ `briefing` |
| `runner/smoke.py` | เพิ่ม `@case` ใหม่ทั้งสองฝั่ง เก็บทั้งสอง |
| `AgentTools.cs` | บรรทัดลงทะเบียน tool ใน `All()`/`Core()` ให้ต่อกันทั้งสองฝั่ง เช่น `.Concat(CraftingToolDefs()).Concat(QolToolDefs())` |
| `ModEntry.cs` | PR #52 และ #25 และ #18 แก้พร้อมกัน ต้องอ่านทีละจุด |
| `runner/tests/conftest.py` | เหมือนกันทุก PR ที่เพิ่ม (add/add) เลือกฝั่งใดก็ได้ |
| `TrashTools.cs` | #86 (#14) และ #87 (#54) สร้างไฟล์ใหม่ชื่อเดียวกัน ตอน merge ตัวที่สองให้รวมหรือเปลี่ยนชื่อไฟล์/class ตัวหนึ่ง (`search_trash` ของ #54 vs `trash` ของ #14) |
| `studio/overlay.html` | #18 #32 #34 แก้พร้อมกัน #34 เขียนไฟล์ใหม่เกือบทั้งหมดเป็นระบบแปลภาษา merge #34 ท้ายสุดของกลุ่ม B แล้วเอาการแก้ของ #18/#32 ไปใส่ในตาราง i18n |

## จุดที่ควรเช็กหลัง merge

- `ToolContract` (จาก #82) จะเตือนตอนเริ่มสำหรับ tool ที่ไม่มี `reason` เช่น `close_menu`
- #75 (briefing) ใช้ provider registry: ช่องว่างสำหรับ #22 (10), #25 (35), #11 (60), #21 (70) ถ้า PR เหล่านั้น merge แล้วให้ตรวจว่า hook เข้ามาจริง
- `GoToBed` ถูก hook โดย #22 #25 #29 #32 #53 ตรวจว่า hook ไม่ทำงานซ้ำ
- `talk_to` ถูกแก้ทั้งใน #9 และ #11 ตรวจพฤติกรรมหลังรวม
- `/api/state` ได้ฟิลด์ใหม่จาก #32 (`paused`, `stopAfterDay`, `stopNow`) watchdog ใน #65 ควรอ่าน `paused`

## ปัญหาที่เจอแต่ยังไม่ได้แก้ (นอกขอบเขต)

- #32: HTTP API ส่ง CORS `*` เว็บหน้าไหนบนเครื่องก็เรียก endpoint ควบคุม/debug ได้
- #24: `backup_save` อาจ copy save ที่เขียนไม่เสร็จ
- #17: `tools/call` ที่ไม่มี `params.name` หรือ body ผิดรูปตอบ HTTP 500
- #15: Scythe ฟันครั้งเดียว และ `FindItem` จับ "axe" ชนกับ "Pickaxe"
- #16: ตัวอย่าง "Spring 20 Cauliflower 12 days" ในโจทย์ขัดกันเอง ใช้ขอบเขต Spring 16
- #23: ข้อมูลทดสอบเป็นข้อมูลสังเคราะห์
- #30: `run_days.last_actions` ไม่กรองบรรทัด JSON ที่ไม่ใช่ dict และไม่กรองตามผู้เล่น

## Track C (ไม่ได้ทำ ต้องการข้อมูลจากคุณ)

#1, #12, #55: ต้องให้คุณกำหนด spec ให้ชัด · #41: ต้องมี CLI และ config ของผู้ให้บริการ GPT ฝั่งคุณ · #48: ต้องรัน warm-up 3 วันจริง
