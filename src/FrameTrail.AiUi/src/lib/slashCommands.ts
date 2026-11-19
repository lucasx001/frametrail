import type { LocaleCode } from '../bridge/protocol';

export interface SlashCommandItem {
  command: string;
  descriptionZh: string;
  descriptionEn: string;
  templateZh: string;
  templateEn: string;
}

export const SLASH_COMMANDS: SlashCommandItem[] = [
  {
    command: '/silence',
    descriptionZh: '自动检测音轨并剪除 >1s 静音停顿',
    descriptionEn: 'Scan audio tracks and cut >1s silent pauses',
    templateZh: '自动清理静音停顿',
    templateEn: 'Automatically remove silent pauses',
  },
  {
    command: '/clicks',
    descriptionZh: '沿录制鼠标点击坐标添加 1.8× 聚焦放大',
    descriptionEn: 'Add 1.8× zoom targeting recorded mouse clicks',
    templateZh: '在关键点击处添加放大运镜',
    templateEn: 'Add 1.8x zoom at key recorded clicks',
  },
  {
    command: '/zoom',
    descriptionZh: '在当前位置添加 1.8× 镜头放大',
    descriptionEn: 'Add 1.8× camera zoom at current playhead',
    templateZh: '在 @{time} 添加 1.8 倍放大特效',
    templateEn: 'Add 1.8x zoom at @{time}',
  },
  {
    command: '/focus',
    descriptionZh: '在当前位置添加聚光灯背景虚化',
    descriptionEn: 'Add spotlight focus blur at current playhead',
    templateZh: '在 @{time} 添加聚焦虚化',
    templateEn: 'Add focus blur at @{time}',
  },
  {
    command: '/mask',
    descriptionZh: '在当前位置添加隐私打码遮挡',
    descriptionEn: 'Add privacy blur mask at current playhead',
    templateZh: '在 @{time} 添加打码遮挡',
    templateEn: 'Add privacy mask at @{time}',
  },
  {
    command: '/tilt',
    descriptionZh: '在当前位置添加 3D 屏幕倾斜运镜',
    descriptionEn: 'Add 3D screen tilt motion at current playhead',
    templateZh: '在 @{time} 添加 3D 倾斜特效',
    templateEn: 'Add 3D tilt effect at @{time}',
  },
  {
    command: '/portrait',
    descriptionZh: '一键切换 9:16 竖屏短视频画布',
    descriptionEn: 'Switch canvas to 9:16 portrait layout',
    templateZh: '把画布改为 9:16 竖屏',
    templateEn: 'Switch canvas to 9:16 portrait',
  },
  {
    command: '/speed',
    descriptionZh: '调整当前片段播放倍速',
    descriptionEn: 'Adjust playback speed of current clip',
    templateZh: '把片段倍速设为 2x',
    templateEn: 'Set clip speed to 2x',
  },
  {
    command: '/cut',
    descriptionZh: '剪除指定时间段',
    descriptionEn: 'Cut a specific time range',
    templateZh: '剪掉 {time} 到 {end} ',
    templateEn: 'Cut from {time} to {end} ',
  },
];

function formatCompactSeconds(sec: number): string {
  const rounded = Math.round(sec * 100) / 100;
  return `${Number(rounded.toFixed(2))}s`;
}

export function expandSlashCommandTemplate(
  item: SlashCommandItem,
  locale: LocaleCode,
  playheadSec: number,
  durationSec: number
): string {
  const cur = Math.max(0, playheadSec);
  const end = durationSec > 0 ? Math.min(durationSec, cur + 2) : cur + 2;
  const template = locale === 'en' ? item.templateEn : item.templateZh;
  return template
    .replace('{time}', formatCompactSeconds(cur))
    .replace('{end}', formatCompactSeconds(end));
}
