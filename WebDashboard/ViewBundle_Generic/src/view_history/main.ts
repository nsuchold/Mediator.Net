import 'typeface-roboto/index.css'
import 'material-design-icons-iconfont/dist/material-design-icons.css'
import Vue from 'vue'
import '../plugins/vuetify'
import ViewHistory from './ViewHistory.vue'

Vue.config.productionTip = false

import { setupDashboardEnv } from '../debug'

if (process.env.NODE_ENV === 'development') {
  setupDashboardEnv('history')
}

const app = new Vue({
  el: '#app',
  render(h) {
    return h(ViewHistory)
  },
})
