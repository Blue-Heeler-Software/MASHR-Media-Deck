package com.mediadeck.remote;

import android.app.Activity;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.RectF;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.view.WindowInsets;
import android.widget.Button;
import android.widget.EditText;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.SeekBar;
import android.widget.TextView;
import android.widget.Toast;
import org.json.JSONObject;
import java.io.BufferedReader;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.IOException;
import java.net.HttpURLConnection;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.URL;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class MainActivity extends Activity {
    private static final int BG=Color.rgb(9,10,16), CARD=Color.rgb(24,25,36), INK=Color.rgb(246,244,255), MUTED=Color.rgb(161,161,179), PURPLE=Color.rgb(167,139,250);
    private final Handler ui=new Handler(Looper.getMainLooper());
    private final ExecutorService io=Executors.newSingleThreadExecutor();
    private final Runnable poll=()->refresh(false);
    private ImageView artwork;
    private TextView source,title,artist,status,elapsed,remaining;
    private Button previous,play,next,shuffle,repeat,altTab;
    private SeekBar timeline;
    private String base,lastTrack="";
    private boolean running,requestPending,userSeeking,altHeld;
    private long durationMs;

    @Override public void onCreate(Bundle state){
        super.onCreate(state);
        getWindow().setStatusBarColor(BG); getWindow().setNavigationBarColor(BG);
        base=getPreferences(0).getString("pc","");
        build();
    }
    @Override protected void onResume(){super.onResume();running=true;refresh(true);}
    @Override protected void onPause(){running=false;ui.removeCallbacks(poll);if(altHeld)endAltGesture();super.onPause();}
    @Override protected void onDestroy(){io.shutdownNow();super.onDestroy();}

    private void build(){
        ScrollView scroll=new ScrollView(this);scroll.setFillViewport(true);scroll.setBackgroundColor(BG);scroll.setClipToPadding(false);
        LinearLayout root=new LinearLayout(this);root.setOrientation(LinearLayout.VERTICAL);
        final int side=dp(16),top=dp(10),bottom=dp(14);root.setPadding(side,top,side,bottom);
        root.setOnApplyWindowInsetsListener((v,insets)->{
            int insetTop,insetBottom;
            if(Build.VERSION.SDK_INT>=30){android.graphics.Insets bars=insets.getInsets(WindowInsets.Type.systemBars());insetTop=bars.top;insetBottom=bars.bottom;}
            else{insetTop=insets.getSystemWindowInsetTop();insetBottom=insets.getSystemWindowInsetBottom();}
            v.setPadding(side,top+insetTop,side,bottom+insetBottom);return insets;
        });
        scroll.addView(root,new ScrollView.LayoutParams(-1,-2));

        LinearLayout topBar=new LinearLayout(this);topBar.setGravity(Gravity.CENTER_VERTICAL);
        TextView brand=text("MEDIADECK",20,INK,true);brand.setLetterSpacing(.16f);topBar.addView(brand,new LinearLayout.LayoutParams(0,dp(46),1));
        Button settings=button("PC SETTINGS");settings.setTextSize(12);settings.setOnClickListener(v->openPcSettings());topBar.addView(settings,new LinearLayout.LayoutParams(-2,dp(42)));root.addView(topBar);
        status=text("CONNECTING TO PC...",14,MUTED,true);status.setPadding(dp(3),dp(2),0,dp(8));root.addView(status);
        LinearLayout card=new LinearLayout(this);card.setOrientation(LinearLayout.VERTICAL);card.setPadding(dp(14),dp(14),dp(14),dp(15));card.setBackground(round(CARD,22));
        artwork=new ImageView(this);artwork.setScaleType(ImageView.ScaleType.CENTER_CROP);artwork.setBackground(round(Color.rgb(42,38,58),18));
        int artHeight=Math.min(dp(150),(int)(getResources().getDisplayMetrics().heightPixels*.18f));card.addView(artwork,new LinearLayout.LayoutParams(-1,artHeight));
        source=text("PC MEDIA",11,PURPLE,true);source.setLetterSpacing(.12f);source.setPadding(0,dp(12),0,dp(4));card.addView(source);
        title=text("Waiting for PC media",22,INK,true);title.setMaxLines(2);card.addView(title);
        artist=text("Start YouTube Music or another player on the PC",14,MUTED,false);artist.setMaxLines(1);artist.setPadding(0,dp(3),0,dp(7));card.addView(artist);

        timeline=new SeekBar(this);timeline.setMax(1000);timeline.setProgressTintList(android.content.res.ColorStateList.valueOf(PURPLE));timeline.setThumbTintList(android.content.res.ColorStateList.valueOf(PURPLE));
        timeline.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener(){public void onProgressChanged(SeekBar b,int p,boolean fromUser){if(fromUser)elapsed.setText(formatTime(durationMs*p/1000));}public void onStartTrackingTouch(SeekBar b){userSeeking=true;}public void onStopTrackingTouch(SeekBar b){userSeeking=false;seekTo(durationMs*b.getProgress()/1000);}});card.addView(timeline,new LinearLayout.LayoutParams(-1,dp(30)));
        LinearLayout times=new LinearLayout(this);elapsed=text("0:00",11,MUTED,false);remaining=text("-0:00",11,MUTED,false);times.addView(elapsed,new LinearLayout.LayoutParams(0,-2,1));remaining.setGravity(Gravity.END);times.addView(remaining,new LinearLayout.LayoutParams(0,-2,1));times.setPadding(dp(5),0,dp(5),dp(8));card.addView(times);

        LinearLayout transport=new LinearLayout(this);transport.setGravity(Gravity.CENTER);previous=largeAction("◀  PREV","previous",14);previous.setOnClickListener(v->{if(altHeld)sendKeyCommand("arrowleft");else control("previous");});addWeighted(transport,previous);play=largeAction("PLAY","play",16);play.setTextColor(Color.BLACK);play.setBackground(round(PURPLE,20));addWeighted(transport,play);next=largeAction("NEXT  ▶","next",14);next.setOnClickListener(v->{if(altHeld)sendKeyCommand("arrowright");else control("next");});addWeighted(transport,next);card.addView(transport,new LinearLayout.LayoutParams(-1,dp(62)));
        LinearLayout seekRow=new LinearLayout(this);seekRow.setGravity(Gravity.CENTER);seekRow.setPadding(0,dp(7),0,0);addWeighted(seekRow,largeAction("↶  10 SEC","back10",14));addWeighted(seekRow,largeAction("10 SEC  ↷","forward10",14));card.addView(seekRow,new LinearLayout.LayoutParams(-1,dp(55)));
        LinearLayout volumeRow=new LinearLayout(this);volumeRow.setGravity(Gravity.CENTER);volumeRow.setPadding(0,dp(7),0,0);addWeighted(volumeRow,largeAction("MUTE","mute",13));addWeighted(volumeRow,largeAction("VOL  −","volumedown",13));addWeighted(volumeRow,largeAction("VOL  +","volumeup",13));card.addView(volumeRow,new LinearLayout.LayoutParams(-1,dp(53)));
        LinearLayout modeRow=new LinearLayout(this);modeRow.setGravity(Gravity.CENTER);modeRow.setPadding(0,dp(7),0,0);shuffle=largeAction("SHUFFLE","shuffle",12);addWeighted(modeRow,shuffle);repeat=largeAction("REPEAT","repeat",12);addWeighted(modeRow,repeat);Button stop=largeAction("STOP","stop",13);stop.setTextColor(Color.rgb(254,202,202));addWeighted(modeRow,stop);card.addView(modeRow,new LinearLayout.LayoutParams(-1,dp(51)));
        altTab=largeAction("HOLD ALT + TAB   •   TAP PREV / NEXT","alttab",13);altTab.setTextColor(Color.BLACK);altTab.setBackground(round(Color.rgb(251,191,36),20));altTab.setOnTouchListener((v,event)->{if(event.getActionMasked()==MotionEvent.ACTION_DOWN){v.getParent().requestDisallowInterceptTouchEvent(true);beginAltGesture();return true;}if(event.getActionMasked()==MotionEvent.ACTION_UP||event.getActionMasked()==MotionEvent.ACTION_CANCEL){v.getParent().requestDisallowInterceptTouchEvent(false);endAltGesture();return true;}return true;});LinearLayout.LayoutParams altLp=new LinearLayout.LayoutParams(-1,dp(52));altLp.setMargins(dp(3),dp(8),dp(3),0);card.addView(altTab,altLp);
        SwipeReplayView replay=new SwipeReplayView(()->{android.util.Log.i("MediaDeck","instant replay swipe completed");sendKeyCommand("instantreplay");Toast.makeText(this,"Instant Replay shortcut sent to PC",Toast.LENGTH_SHORT).show();});LinearLayout.LayoutParams replayLp=new LinearLayout.LayoutParams(-1,dp(56));replayLp.setMargins(dp(3),dp(8),dp(3),0);card.addView(replay,replayLp);root.addView(card);
        setContentView(scroll);root.requestApplyInsets();
    }

    private void refresh(boolean showConnecting){
        if(requestPending)return;requestPending=true;ui.removeCallbacks(poll);
        if(showConnecting){status.setText(base.isEmpty()?"FINDING PC...":"CONNECTING TO PC...");status.setTextColor(MUTED);}
        io.execute(()->{try{JSONObject data=new JSONObject(get("/api/now"));ui.post(()->apply(data));}catch(Exception first){try{String discovered=discoverPc();if(discovered!=null){base=discovered;getPreferences(0).edit().putString("pc",base).apply();JSONObject data=new JSONObject(get("/api/now"));ui.post(()->apply(data));return;}}catch(Exception ignored){}ui.post(this::showError);}});
    }
    private void apply(JSONObject data){
        requestPending=false;
        String newTitle=data.optString("title","Nothing playing"),newArtist=data.optString("artist","Start media on your PC");boolean playing=data.optBoolean("playing");
        source.setText(friendlySource(data.optString("source","PC MEDIA")));title.setText(newTitle);artist.setText(newArtist);play.setText(playing?"PAUSE":"PLAY");play.setOnClickListener(v->control(playing?"pause":"play"));
        durationMs=data.optLong("durationMs");long positionMs=Math.min(data.optLong("positionMs"),durationMs);if(!userSeeking)timeline.setProgress(durationMs>0?(int)(positionMs*1000/durationMs):0);timeline.setEnabled(durationMs>0);elapsed.setText(formatTime(positionMs));remaining.setText("-"+formatTime(Math.max(0,durationMs-positionMs)));
        shuffle.setText(data.optBoolean("shuffle")?"SHUFFLE ON":"SHUFFLE");String repeatMode=data.optString("repeat","none");repeat.setText(repeatMode.equals("track")?"REPEAT 1":repeatMode.equals("list")?"REPEAT ALL":"REPEAT");
        status.setText("●  PC CONNECTED");status.setTextColor(PURPLE);
        String key=newTitle+'\n'+newArtist;if(!key.equals(lastTrack)){lastTrack=key;loadArtwork();}
        schedule();
    }
    private void showError(){requestPending=false;status.setText("●  PC OFFLINE — TAP PC SETTINGS");status.setTextColor(Color.rgb(248,113,113));schedule();}
    private void schedule(){if(running){ui.removeCallbacks(poll);ui.postDelayed(poll,2500);}}
    private void loadArtwork(){io.execute(()->{try(InputStream in=new URL(url("/api/art?track="+System.currentTimeMillis())).openStream()){Bitmap bitmap=BitmapFactory.decodeStream(in);if(bitmap!=null)ui.post(()->artwork.setImageBitmap(bitmap));}catch(Exception ignored){}});}
    private void control(String command){io.execute(()->{try{post("/api/control/"+command);}catch(Exception ignored){}ui.postDelayed(()->refresh(false),180);});}
    private void sendKeyCommand(String command){io.execute(()->{try{post("/api/control/"+command);}catch(Exception ignored){}});}
    private void beginAltGesture(){if(altHeld)return;altHeld=true;altTab.setText("ALT HELD   •   CHOOSE A WINDOW");altTab.setBackground(round(Color.rgb(248,113,113),20));previous.setText("◀  WINDOW");next.setText("WINDOW  ▶");sendKeyCommand("altdown");}
    private void endAltGesture(){if(!altHeld)return;altHeld=false;sendKeyCommand("altup");altTab.setText("HOLD ALT + TAB   •   TAP PREV / NEXT");altTab.setBackground(round(Color.rgb(251,191,36),20));previous.setText("◀  PREV");next.setText("NEXT  ▶");}
    private void seekTo(long positionMs){io.execute(()->{try{post("/api/seek?positionMs="+positionMs);}catch(Exception ignored){}ui.postDelayed(()->refresh(false),180);});}
    private Button action(String label,String command){Button b=button(label);b.setOnClickListener(v->control(command));return b;}
    private Button largeAction(String label,String command,int size){Button b=action(label,command);b.setTextSize(size);b.setSingleLine(true);b.setPadding(dp(4),0,dp(4),0);return b;}
    private void openPcSettings(){EditText input=new EditText(this);input.setSingleLine();input.setText(base);input.setSelectAllOnFocus(true);input.setTextSize(18);input.setPadding(dp(18),dp(12),dp(18),dp(12));new android.app.AlertDialog.Builder(this).setTitle("Gaming PC address").setMessage("Enter the PC's local network address").setView(input).setNegativeButton("CANCEL",null).setPositiveButton("CONNECT",(dialog,which)->{String value=input.getText().toString().trim();if(!value.isEmpty()){base=value;getPreferences(0).edit().putString("pc",base).apply();lastTrack="";refresh(true);}}).show();}
    private void addWeighted(LinearLayout row,View view){LinearLayout.LayoutParams lp=new LinearLayout.LayoutParams(0,-1,1);lp.setMargins(dp(3),0,dp(3),0);row.addView(view,lp);}
    private String formatTime(long millis){long total=Math.max(0,millis/1000),hours=total/3600,minutes=(total%3600)/60,seconds=total%60;return hours>0?String.format(java.util.Locale.US,"%d:%02d:%02d",hours,minutes,seconds):String.format(java.util.Locale.US,"%d:%02d",minutes,seconds);}
    private String friendlySource(String value){String x=value.toLowerCase();if(x.contains("vlc"))return "VLC • PC";if(x.contains("brave"))return "BRAVE • PC";if(x.contains("chrome"))return "CHROME • PC";if(x.contains("spotify"))return "SPOTIFY • PC";return "PC MEDIA";}
    private String url(String path){return "http://"+base+":43821"+path;}
    private String discoverPc(){try(DatagramSocket socket=new DatagramSocket()){socket.setBroadcast(true);socket.setSoTimeout(1400);byte[] query="MEDIADECK_DISCOVER".getBytes(java.nio.charset.StandardCharsets.UTF_8);socket.send(new DatagramPacket(query,query.length,InetAddress.getByName("255.255.255.255"),43822));byte[] buffer=new byte[64];DatagramPacket reply=new DatagramPacket(buffer,buffer.length);socket.receive(reply);String message=new String(reply.getData(),0,reply.getLength(),java.nio.charset.StandardCharsets.UTF_8);return message.equals("MEDIADECK:43821")?reply.getAddress().getHostAddress():null;}catch(Exception ignored){return null;}}
    private String get(String path)throws Exception{if(base.isEmpty())throw new IOException("No saved PC address");HttpURLConnection c=(HttpURLConnection)new URL(url(path)).openConnection();c.setConnectTimeout(1800);c.setReadTimeout(1800);try(BufferedReader r=new BufferedReader(new InputStreamReader(c.getInputStream()))){return r.readLine();}finally{c.disconnect();}}
    private void post(String path)throws Exception{HttpURLConnection c=(HttpURLConnection)new URL(url(path)).openConnection();c.setRequestMethod("POST");c.setDoOutput(true);c.setConnectTimeout(1800);c.getOutputStream().close();c.getInputStream().close();c.disconnect();}
    private TextView text(String value,int size,int color,boolean bold){TextView v=new TextView(this);v.setText(value);v.setTextSize(size);v.setTextColor(color);v.setTypeface(Typeface.DEFAULT,bold?Typeface.BOLD:Typeface.NORMAL);v.setLineSpacing(0,1.1f);return v;}
    private Button button(String value){Button b=new Button(this);b.setText(value);b.setTextColor(INK);b.setTextSize(11);b.setTypeface(Typeface.DEFAULT_BOLD);b.setMinHeight(0);b.setMinWidth(0);b.setPadding(dp(16),0,dp(16),0);b.setBackground(round(Color.rgb(47,44,67),20));return b;}
    private GradientDrawable round(int color,int radius){GradientDrawable d=new GradientDrawable();d.setColor(color);d.setCornerRadius(dp(radius));return d;}
    private int dp(int value){return Math.round(value*getResources().getDisplayMetrics().density);}

    private final class SwipeReplayView extends View {
        private final Paint track=new Paint(Paint.ANTI_ALIAS_FLAG),fill=new Paint(Paint.ANTI_ALIAS_FLAG),handle=new Paint(Paint.ANTI_ALIAS_FLAG),label=new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Runnable activate;private float progress,startY;private boolean tracking,complete;
        SwipeReplayView(Runnable activate){super(MainActivity.this);this.activate=activate;setContentDescription("Swipe left to right to save instant replay");setFocusable(true);track.setColor(Color.rgb(20,83,45));fill.setColor(Color.rgb(34,197,94));handle.setColor(Color.rgb(236,253,245));label.setColor(Color.WHITE);label.setTextAlign(Paint.Align.CENTER);label.setTypeface(Typeface.DEFAULT_BOLD);label.setTextSize(getResources().getDisplayMetrics().scaledDensity*14);}
        @Override protected void onDraw(Canvas canvas){super.onDraw(canvas);float radius=getHeight()/2f;RectF bounds=new RectF(0,0,getWidth(),getHeight());canvas.drawRoundRect(bounds,radius,radius,track);float knob=dp(22),left=knob,right=getWidth()-knob,x=left+(right-left)*progress;if(progress>0){RectF active=new RectF(0,0,x,getHeight());canvas.drawRoundRect(active,radius,radius,fill);}canvas.drawCircle(x,getHeight()/2f,knob,handle);String text=complete?"REPLAY SHORTCUT SENT":tracking?"KEEP SWIPING  "+Math.round(progress*100)+"%":"SWIPE TO SAVE REPLAY   →";Paint.FontMetrics fm=label.getFontMetrics();canvas.drawText(text,getWidth()/2f,getHeight()/2f-(fm.ascent+fm.descent)/2,label);}
        @Override public boolean onTouchEvent(MotionEvent event){float knob=dp(22),usable=Math.max(1,getWidth()-2*knob);switch(event.getActionMasked()){case MotionEvent.ACTION_DOWN:if(complete||event.getX()>getWidth()*.30f)return true;tracking=true;startY=event.getY();progress=Math.max(0,Math.min(1,(event.getX()-knob)/usable));getParent().requestDisallowInterceptTouchEvent(true);invalidate();return true;case MotionEvent.ACTION_MOVE:if(!tracking)return true;if(Math.abs(event.getY()-startY)>dp(42)){cancelSwipe();return true;}progress=Math.max(0,Math.min(1,(event.getX()-knob)/usable));invalidate();return true;case MotionEvent.ACTION_UP:if(tracking&&progress>=.85f){tracking=false;complete=true;progress=1;performHapticFeedback(android.view.HapticFeedbackConstants.LONG_PRESS);activate.run();invalidate();postDelayed(()->{complete=false;progress=0;invalidate();},2200);}else cancelSwipe();getParent().requestDisallowInterceptTouchEvent(false);return true;case MotionEvent.ACTION_CANCEL:cancelSwipe();getParent().requestDisallowInterceptTouchEvent(false);return true;default:return true;}}
        private void cancelSwipe(){tracking=false;progress=0;invalidate();}
    }
}
