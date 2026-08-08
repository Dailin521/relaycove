# RelayCove UI Component Specification v1.0

## 目标

本文定义 RelayCove 桌面客户端 UI 组件标准，用于 Codex / Claude Code 实现
UI 重构。

原则：

-   组件职责单一
-   状态明确
-   样式统一
-   支持长期扩展

------------------------------------------------------------------------

# 1. NavigationRail

## 职责

左侧主导航区域。

## 结构

    NavigationRail
     ├ Avatar
     ├ ChatButton
     ├ ContactButton
     ├ ChannelButton
     ├ NotificationButton
     ├ FileButton
     ├ SettingButton
     └ MoreButton

## 状态

-   Normal
-   Hover
-   Selected
-   Disabled

## 设计要求

-   宽度固定
-   图标优先
-   当前入口明显
-   不承载业务逻辑

------------------------------------------------------------------------

# 2. ConversationPanel

## 职责

展示全部会话。

结构：

    SearchBox
    FilterTabs
    ConversationGroups

分组：

-   公开频道
-   私有频道
-   私聊

------------------------------------------------------------------------

# 3. ConversationItem

## 数据

必须支持：

-   Avatar/Icon
-   Name
-   Preview
-   Timestamp
-   UnreadCount
-   MuteState

## 状态

### Normal

普通显示。

### Hover

鼠标经过。

### Selected

当前打开。

### Unread

显示未读数量。

### Muted

显示静音状态。

------------------------------------------------------------------------

# 4. ChatHeader

包含：

-   标题
-   描述
-   成员数量
-   搜索
-   置顶
-   通知
-   更多

------------------------------------------------------------------------

# 5. MessageItem

支持：

-   TextMessage
-   ImageMessage
-   FileMessage
-   ReplyMessage
-   SystemMessage

状态：

    Sending
    Sent
    Failed
    Retrying
    Deleted

------------------------------------------------------------------------

# 6. AttachmentCard

文件消息组件。

展示：

-   文件名
-   大小
-   类型
-   状态
-   操作按钮

状态：

-   Uploading
-   Completed
-   Failed
-   Cancelled

------------------------------------------------------------------------

# 7. Composer

底部输入组件。

功能：

-   多行输入
-   Emoji
-   @成员
-   图片
-   文件
-   发送

状态：

-   Empty
-   Typing
-   Sending
-   Disabled

------------------------------------------------------------------------

# 8. 组件开发原则

禁止：

-   页面中大量硬编码 UI
-   一个窗口包含全部逻辑
-   使用图片模拟控件

推荐：

-   Style
-   Template
-   DataBinding
-   独立 ViewModel
