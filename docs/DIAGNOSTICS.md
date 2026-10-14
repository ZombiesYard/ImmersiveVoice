# 日志与反馈

F12菜单中的“保存排查日志”默认开启。日志写入客户端目录下的 `BepInEx/ImmersiveVoiceLogs/voice-*.log`，保留最近10个文件，每个最多4 MiB。写入队列有上限，达到容量后停止追加或记录丢弃数，不阻塞主线程。

日志区分事件候选、概率判断、等待、取消、发声尝试及Speaker确认播放。`Request`连接同一次事件；`Time`只属于本客户端时钟，不能直接用两台电脑的数值相减测网络延迟。

常见原因：

| 原因 | 含义 |
| --- | --- |
| NoMatchingBank | 声线没有对应片段，或没有匹配当前状态的片段 |
| ReloadProbability | 本次换弹未通过概率抽签 |
| NativeBreath / NativeSpeakerBusy | 呼吸或其他原版发声占用 |
| RecentDamage | 暂时等待最近受击保护结束 |
| ActionEnded / Expired | 动作已结束或候选过期 |
| SameHealthEpisode | 同段伤情不需要重复播报 |
| AudioNotPlaying / AudioTooQuiet / OutsideHearingRange | NPC音源未播放、被静音/衰减或超出听音范围 |
| UnmappedNpcPhrase / NoReplyToThisMeaning | 未映射的来话，或不需要接话的生理声/应答 |
| NotAddressedNearby / AwaitMatchingContextOrAction | 来话不在近处交流范围，或正在等待动作与状态吻合 |
| NpcTurnLimit / NpcCapacity | 同一NPC已完成两轮回应，或近期NPC缓存已满 |
| NearbyPlayerReplied | 附近队友已经发出同类回应 |
| ReplyCooldown / NpcStillSpeaking | 回应冷却中，或NPC还没说完 |
| NpcExchangeEnded | 对象离开、死亡、事件过期或其他情况使回应失效 |

独立日志不记录聊天、录音、昵称、账号标识、完整物品列表或绝对坐标。它会包含声线名称、设置、距离、速度和事件时序。其他插件写入的BepInEx主日志不受本Mod控制。

反馈时提供游戏/SPT/Fika/插件版本、角色声线、发生的动作及对应时间段。上传前检查日志，移除账号信息、网络地址、个人路径和无关插件内容；不要公开完整个人配置、存档或游戏目录。

开发者可在源码根目录运行：

```text
python tools/summarize_voice_log.py PATH_TO_VOICE_LOG --timeline
```

多端核对使用各自同一场战局的日志。记录中的播放确认表示原生回调发生，不等于已由人耳确认听感、声学遮挡或远端实际可听。

`NpcHeard`表示音频观察结果，不能计作本机已经说话。`NpcEpisode`连接同一互动；实际发声仍以带有`Source=Auto`的`Played`记录为准。摘要中的`npc_hearing_decisions`汇总听音和接纳原因。

`NpcHeard`还记录NPC角色、关系和来话语义；`NpcDecision`记录等待动作、选定反应及回合限制。`NpcSummary`包含固定容量、接收次数、4Hz采样次数和NPC Tick平均/最大耗时；这不包含整个Mod或游戏音频系统的全部开销。`NpcCapacity`汇总容量丢弃数；`NpcFault`表示本局NPC模块因异常停止，其余导演功能继续。摘要新增`npc_relations`、`npc_dialogue_decisions`和`npc_performance`。
