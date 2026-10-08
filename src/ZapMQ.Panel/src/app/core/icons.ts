import { inject, provideAppInitializer } from '@angular/core';
import { MatIconRegistry } from '@angular/material/icon';
import { DomSanitizer } from '@angular/platform-browser';

import accountCircle from '@material-symbols/svg-400/rounded/account_circle.svg';
import arrowBack from '@material-symbols/svg-400/rounded/arrow_back.svg';
import arrowDownward from '@material-symbols/svg-400/rounded/arrow_downward.svg';
import arrowUpward from '@material-symbols/svg-400/rounded/arrow_upward.svg';
import block from '@material-symbols/svg-400/rounded/block.svg';
import bolt from '@material-symbols/svg-400/rounded/bolt.svg';
import checkCircle from '@material-symbols/svg-400/rounded/check_circle.svg';
import chevronRight from '@material-symbols/svg-400/rounded/chevron_right.svg';
import close from '@material-symbols/svg-400/rounded/close.svg';
import cloudOff from '@material-symbols/svg-400/rounded/cloud_off.svg';
import contentCopy from '@material-symbols/svg-400/rounded/content_copy.svg';
import darkMode from '@material-symbols/svg-400/rounded/dark_mode.svg';
import dashboard from '@material-symbols/svg-400/rounded/dashboard.svg';
import deleteIcon from '@material-symbols/svg-400/rounded/delete.svg';
import deviceHub from '@material-symbols/svg-400/rounded/device_hub.svg';
import dns from '@material-symbols/svg-400/rounded/dns.svg';
import error from '@material-symbols/svg-400/rounded/error.svg';
import hourglassEmpty from '@material-symbols/svg-400/rounded/hourglass_empty.svg';
import inbox from '@material-symbols/svg-400/rounded/inbox.svg';
import info from '@material-symbols/svg-400/rounded/info.svg';
import keyboardArrowDown from '@material-symbols/svg-400/rounded/keyboard_arrow_down.svg';
import lan from '@material-symbols/svg-400/rounded/lan.svg';
import lightMode from '@material-symbols/svg-400/rounded/light_mode.svg';
import logout from '@material-symbols/svg-400/rounded/logout.svg';
import mail from '@material-symbols/svg-400/rounded/mail.svg';
import menu from '@material-symbols/svg-400/rounded/menu.svg';
import moreVert from '@material-symbols/svg-400/rounded/more_vert.svg';
import powerSettingsNew from '@material-symbols/svg-400/rounded/power_settings_new.svg';
import pause from '@material-symbols/svg-400/rounded/pause.svg';
import pending from '@material-symbols/svg-400/rounded/pending.svg';
import playArrow from '@material-symbols/svg-400/rounded/play_arrow.svg';
import refresh from '@material-symbols/svg-400/rounded/refresh.svg';
import replay from '@material-symbols/svg-400/rounded/replay.svg';
import schedule from '@material-symbols/svg-400/rounded/schedule.svg';
import search from '@material-symbols/svg-400/rounded/search.svg';
import settings from '@material-symbols/svg-400/rounded/settings.svg';
import skull from '@material-symbols/svg-400/rounded/skull.svg';
import speed from '@material-symbols/svg-400/rounded/speed.svg';
import stacks from '@material-symbols/svg-400/rounded/stacks.svg';
import sync from '@material-symbols/svg-400/rounded/sync.svg';
import tune from '@material-symbols/svg-400/rounded/tune.svg';
import verified from '@material-symbols/svg-400/rounded/verified.svg';
import visibility from '@material-symbols/svg-400/rounded/visibility.svg';
import warning from '@material-symbols/svg-400/rounded/warning.svg';
import memory from '@material-symbols/svg-400/rounded/memory.svg';
import history from '@material-symbols/svg-400/rounded/history.svg';
import restartAlt from '@material-symbols/svg-400/rounded/restart_alt.svg';
import add from '@material-symbols/svg-400/rounded/add.svg';
import remove from '@material-symbols/svg-400/rounded/remove.svg';
import monitorHeart from '@material-symbols/svg-400/rounded/monitor_heart.svg';
import dataObject from '@material-symbols/svg-400/rounded/data_object.svg';
import save from '@material-symbols/svg-400/rounded/save.svg';
import rocketLaunch from '@material-symbols/svg-400/rounded/rocket_launch.svg';
import recycling from '@material-symbols/svg-400/rounded/recycling.svg';
import trendingUp from '@material-symbols/svg-400/rounded/trending_up.svg';
import edit from '@material-symbols/svg-400/rounded/edit.svg';
import terminal from '@material-symbols/svg-400/rounded/terminal.svg';
import download from '@material-symbols/svg-400/rounded/download.svg';
import verticalAlignBottom from '@material-symbols/svg-400/rounded/vertical_align_bottom.svg';
import wrapText from '@material-symbols/svg-400/rounded/wrap_text.svg';
import deleteSweep from '@material-symbols/svg-400/rounded/delete_sweep.svg';
import stop from '@material-symbols/svg-400/rounded/stop.svg';
import folder from '@material-symbols/svg-400/rounded/folder.svg';
import autorenew from '@material-symbols/svg-400/rounded/autorenew.svg';
import settingsApplications from '@material-symbols/svg-400/rounded/settings_applications.svg';
import web from '@material-symbols/svg-400/rounded/web.svg';
import widgets from '@material-symbols/svg-400/rounded/widgets.svg';
import publish from '@material-symbols/svg-400/rounded/publish.svg';
import hub from '@material-symbols/svg-400/rounded/hub.svg';
import openInNew from '@material-symbols/svg-400/rounded/open_in_new.svg';
import deployedCode from '@material-symbols/svg-400/rounded/deployed_code.svg';
import precisionManufacturing from '@material-symbols/svg-400/rounded/precision_manufacturing.svg';

const ICONS: Record<string, string> = {
  terminal, download, vertical_align_bottom: verticalAlignBottom, wrap_text: wrapText, delete_sweep: deleteSweep,
  stop, folder, autorenew, settings_applications: settingsApplications,
  web, widgets, publish,
  hub, open_in_new: openInNew, deployed_code: deployedCode,
  memory, history, restart_alt: restartAlt, add, remove, monitor_heart: monitorHeart, data_object: dataObject, save, rocket_launch: rocketLaunch, recycling, trending_up: trendingUp, edit, precision_manufacturing: precisionManufacturing,
  account_circle: accountCircle, arrow_back: arrowBack, arrow_downward: arrowDownward, arrow_upward: arrowUpward, block, bolt,
  check_circle: checkCircle, chevron_right: chevronRight, close, cloud_off: cloudOff, content_copy: contentCopy, dark_mode: darkMode,
  dashboard, delete: deleteIcon, device_hub: deviceHub, dns, error, hourglass_empty: hourglassEmpty, inbox, info,
  keyboard_arrow_down: keyboardArrowDown, lan, light_mode: lightMode, logout, mail, menu, more_vert: moreVert, power_settings_new: powerSettingsNew, pause, pending,
  play_arrow: playArrow, refresh, replay, schedule, search, settings, skull, speed, stacks, sync, tune, verified, visibility, warning,
};

/** Only the icons listed here travel with the panel; there is no icon font to download. */
export const provideIcons = () =>
  provideAppInitializer(() => {
    const registry = inject(MatIconRegistry);
    const sanitizer = inject(DomSanitizer);
    for (const [name, svg] of Object.entries(ICONS)) {
      registry.addSvgIconLiteral(name, sanitizer.bypassSecurityTrustHtml(svg));
    }
  });
