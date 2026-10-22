# 日志与反馈

F12菜单中的“保存排查日志”默认开启。日志写入客户端目录下的 `BepInEx/ImmersiveVoiceLogs/voice-*.log`，保留最近10个文件，每个最多4 MiB。写入队列有上限，达到容量后停止追加或记录丢弃数，不阻塞主线程。

日志区分事件候选、概率判断、等待、取消、发声尝试及Speaker确认播放。`Request`连接同一次事件；`Time`只属于本客户端时钟，不能直接用两台电脑的数值相减测网络延迟。

2.16.0新增`CombatReport`、`Direction`和`TeamReportVersion=候选/当前`。2.17.0新增`Stage`、`ActionVersion`和`SearchAction`，用于区分共同潜行、一次性搜索/求助/治疗收尾及真实搜索边缘。`TeammateReportedFirst`表示短延迟期间已经实际听见队友先报，因此取消本机候选；`DirectionChanged`、`CombatReportExpired`、`ReportActorUnavailable`、`StealthApproachEnded`和`SquadDangerExpired`表示原证据已经过期、变化或队友槽位已换代。日志仍不记录目标身份或坐标。

`EnemyConversationConsumed`表示该NPC每局一次的精确OnEnemyConversation机会已经消费；当前声线缺音效时仍显示`NoMatchingBank`，之后不会退回普通回骂或其他台词。

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

## 2.13.1诊断补充

`NpcSummary`的`Callbacks`为模块启用时进入采集入口的次数，`Captures`为过滤后接收次数，`NpcCaptures`为其中NPC来话。`RejectedSpeaker`（本机或对象不可用）、`RejectedAudio`、`OutOfRange`、`HumanIgnored`、`BreathIgnored`是累计过滤计数。容量不足另见`NpcCapacity`。每分钟汇总，不逐条记录远处语音。旧日志缺少这些字段时摘要显示null，不能解释成零。

`PlaybackFault`包含请求、短语、异常次数、`Reason`和`Stack`。`Stack`使用“类型.方法@IL位置”，省略异常消息、参数及文件路径；最多16帧、3层异常并有长度上限。单次空引用/Unity对象失效不再立即结束整个导演，失败的事件候选以`NativePlaybackException`取消；已有原生忙碌/无素材拒绝仍走原规则。连续三次原生异常触发`PlaybackRecovery State=Silenced`，成功播放会清除累计异常。`Fault`和`NpcFault`也保留调用位置。摘要中的`fault_counts`统计全部故障，`fault_details`最多保留32条明细。

`Confirmed`只表示已观察到原生说话回调。若回调之后又发生异常，可能同时出现`Confirmed=True`和`NativeException=True`；不能据此保证另一端收到声音。主日志被后续启动覆盖后，应保留对应独立战局日志供排查。

## 2.14.0小队对话诊断

`SquadDialogue`的`HeardCall`表示真实音频与元数据已匹配；`HostGrant`表示正在申请；`HostSelected`仅表示主机授权，不代表已播放。`MetadataUnavailable`表示来话缺少及时匹配的元数据；`DialogueUnavailable`、`DialogueAssignedElsewhere`和`DialogueAwaitingGrant`分别用于协调不可用、已分配他人/授权失效及等待授权。最终仍以带`Source=Auto`的`Played`和`PlaybackResult`核对。

`Peer`是本端固定队友槽，`DialogueSequence`是该来话者的临时计数，不是全局玩家身份。每端时钟独立，序号不能直接跨不同来话者合并。`SquadDialogueSummary`记录当前网络管理器累计发送/接收消息调用数和主机授予次数，以及收尾时的`Ready`；发送调用不等于已交付，客户端`Grants=0`也不代表未收到授权。headless只在BepInEx主日志记录注册、故障和收尾计数。

摘要新增`squad_dialogue_decisions`和`squad_dialogue_network`。`SquadEvidence`增加`CoverReason`、`MoveReason`和带路重复限制剩余时间，可区分缺少相关火力、动作太短与重复抑制。取得授权后动作失效、音效缺失或超时，不会把同一回应转交另一人。NPC互动仍沿用自己的日志和回应去重规则。

## 2.15.0 Ready收尾诊断

`SquadReadyEvidence`记录`AwaitingHeardCover`、`HeardCoverConfirmed`、`WeaponRecovered`和`ClosingSelected`，以及取消原因。`HeardCoverConfirmed`须有对应的原生Covering音频及主机验证的播放回执；`HostSelected`不够。阶段记录不等于Ready已播放，仍须核对`Event=SquadReady`请求、`Source=Auto Phrase=Ready Decision=Played`和`PlaybackResult`。

`NoUsableActionResult`表示动作结束但仍为空枪或装填没有弹药/弹匣变化；`WeaponNoLongerReady`、`WeaponChanged`、`NewDamage`、`SurvivalOrTreatment`、`CoverPeerUnavailable`、`ReadyExpired`、`SquadPaused`分别解释新操作、切枪、受伤、生存/治疗、队友不可感知、超时和暂停。缺音效仍为`NoMatchingBank`，不会寻找替代短语。故障标志已清除但排障操作仍在进行时，继续等待实际操作结束。

收尾证据的`Caller/Epoch/Sequence`与请求中的`DialogueCaller/DialogueEpoch/DialogueSequence`共同标识临时对话；不能只比较Sequence。`CoverActor`是临时网络编号，`ReadyVersion`是本地动作版本。摘要新增`squad_ready_evidence`，请求时间线保留完整临时编号；不记录持久玩家身份、坐标或音频。

新增收尾状态检查不全在`SquadObserver.Tick`内，原有协同计时不可当作整个新功能或整局CPU消耗；性能基准只测纯规则模型。实战应检查四人同时请求、快速取消装填、排障、缺Ready、切枪、菜单、伤害和断线重连。
