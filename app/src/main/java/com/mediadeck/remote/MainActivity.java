package com.mediadeck.remote;

import android.app.Activity;
import android.app.AlertDialog;
import android.app.Dialog;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.ColorFilter;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.PixelFormat;
import android.graphics.Rect;
import android.graphics.RectF;
import android.graphics.Typeface;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.util.Base64;
import android.util.Log;
import android.view.Gravity;
import android.view.HapticFeedbackConstants;
import android.view.MotionEvent;
import android.view.View;
import android.view.Window;
import android.view.WindowInsets;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.GridLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.SeekBar;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.HttpURLConnection;
import java.net.InetAddress;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.security.KeyPair;
import java.security.KeyPairGenerator;
import java.security.spec.MGF1ParameterSpec;
import javax.crypto.Cipher;
import javax.crypto.spec.OAEPParameterSpec;
import javax.crypto.spec.PSource;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashSet;
import java.util.Locale;
import java.util.UUID;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

public final class MainActivity extends Activity {
    private static final int BG=Color.rgb(9,10,16), CARD=Color.rgb(24,25,36), INK=Color.rgb(246,244,255), MUTED=Color.rgb(161,161,179), PURPLE=Color.rgb(167,139,250), SCENE_BLUE=Color.rgb(125,211,252), GOLDEN_BROWN=Color.rgb(184,134,11);
    private final Handler ui=new Handler(Looper.getMainLooper());
    private final ExecutorService io=Executors.newSingleThreadExecutor();
    private final ExecutorService thumbnails=Executors.newFixedThreadPool(3);
    private final Runnable poll=()->refresh(false);
    private ImageView artwork;
    private TextView source,title,artist,status,elapsed,remaining,scenes,youtubeVolumeValue;
    private Button previous,play,next,shuffle,repeat,altTab,backSkip,aheadSkip,previousScene,nextScene;
    private ChapterSeekBar timeline;
    private SeekBar youtubeVolume;
    private SwipeReplayView replay;
    private MicMuteView microphoneMute;
    private String base="",deviceKey="",deviceId="",deviceName="",lastTrack="",pairRequestToken="";
    private KeyPair nearbyPairKey;
    private boolean running,destroyed,requestPending,userSeeking,youtubeVolumeSeeking,altHeld,youtubeAvailable,youtubeJumpAheadEligible,artworkPending,artworkLoaded,replayAvailable,replayEnabled,microphoneAvailable,microphoneMuted;
    private long durationMs,positionMs,lastArtworkAttemptMs;
    private int replaySeconds=120,skipSeconds=10;
    private final ArrayList<MediaChapter> chapters=new ArrayList<>();
    private enum DeckIcon { SETTINGS,LIST,PREVIOUS,PLAY,PAUSE,NEXT,ARROW_LEFT,ARROW_RIGHT,SEEK_BACK,SEEK_FORWARD,MUTE,VOLUME_DOWN,VOLUME_UP,SHUFFLE,REPEAT,STOP,CAMERA,MONITOR,THUMB_UP,THUMB_DOWN,SUBSCRIBE,ALT_TAB }

    @Override public void onCreate(Bundle state){
        super.onCreate(state);
        getWindow().setStatusBarColor(BG);
        getWindow().setNavigationBarColor(BG);
        base=getPreferences(0).getString("pc","");
        deviceKey=getPreferences(0).getString("deviceKey","");
        deviceId=getPreferences(0).getString("deviceId","");
        if(deviceId.isEmpty()){
            deviceId=UUID.randomUUID().toString().replace("-","");
            getPreferences(0).edit().putString("deviceId",deviceId).apply();
        }
        deviceName=deviceDisplayName();
        skipSeconds=Math.max(1,Math.min(120,getPreferences(0).getInt("skipSeconds",10)));
        build();
    }

    @Override protected void onResume(){super.onResume();running=true;refresh(true);}
    @Override protected void onPause(){running=false;ui.removeCallbacks(poll);if(altHeld)endAltGesture();super.onPause();}
    @Override protected void onDestroy(){destroyed=true;running=false;ui.removeCallbacksAndMessages(null);io.shutdownNow();thumbnails.shutdownNow();super.onDestroy();}

    private void build(){
        LinearLayout root=new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(BG);
        final int side=dp(6),top=dp(10),bottom=dp(14);
        root.setPadding(side,top,side,bottom);
        root.setOnApplyWindowInsetsListener((v,insets)->{
            int insetTop,insetBottom;
            if(Build.VERSION.SDK_INT>=30){android.graphics.Insets bars=insets.getInsets(WindowInsets.Type.systemBars());insetTop=bars.top;insetBottom=bars.bottom;}
            else{insetTop=insets.getSystemWindowInsetTop();insetBottom=insets.getSystemWindowInsetBottom();}
            v.setPadding(side,top+insetTop,side,bottom+insetBottom);
            return insets;
        });

        LinearLayout topBar=new LinearLayout(this);
        topBar.setGravity(Gravity.CENTER_VERTICAL);
        LinearLayout brandBlock=new LinearLayout(this);
        brandBlock.setOrientation(LinearLayout.VERTICAL);
        brandBlock.setGravity(Gravity.CENTER_VERTICAL);
        TextView brand=text("MASHR",20,INK,true);
        brand.setLetterSpacing(.16f);
        brandBlock.addView(brand,new LinearLayout.LayoutParams(-1,dp(25)));
        TextView brandSub=text("MEDIA DECK",9,PURPLE,true);
        brandSub.setLetterSpacing(.20f);
        brandBlock.addView(brandSub,new LinearLayout.LayoutParams(-1,dp(17)));
        topBar.addView(brandBlock,new LinearLayout.LayoutParams(0,dp(42),1));
        Button settings=button("PC SETTINGS");
        settings.setTextSize(11);
        setIcon(settings,DeckIcon.SETTINGS,INK,16,false);
        settings.setOnClickListener(v->openPcSettings());
        topBar.addView(settings,new LinearLayout.LayoutParams(-2,dp(42)));
        root.addView(topBar);

        status=text("CONNECTING TO PC...",14,MUTED,true);
        status.setPadding(dp(3),dp(2),0,dp(4));
        root.addView(status);

        LinearLayout card=new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(8),dp(9),dp(8),dp(9));
        card.setBackground(round(CARD,22));
        artwork=new ImageView(this);
        artwork.setScaleType(ImageView.ScaleType.CENTER_CROP);
        artwork.setBackground(round(Color.rgb(42,38,58),18));
        artwork.setContentDescription("Current media artwork. Swipe up for YouTube suggestions.");
        artwork.setOnTouchListener(new View.OnTouchListener(){
            float downX,downY;
            @Override public boolean onTouch(View view,MotionEvent event){
                if(event.getActionMasked()==MotionEvent.ACTION_DOWN){downX=event.getX();downY=event.getY();view.getParent().requestDisallowInterceptTouchEvent(true);return true;}
                if(event.getActionMasked()==MotionEvent.ACTION_UP){view.getParent().requestDisallowInterceptTouchEvent(false);float rise=downY-event.getY(),sideways=Math.abs(downX-event.getX());if(rise>dp(55)&&sideways<dp(100)){view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);showYouTubeSuggestions();}return true;}
                if(event.getActionMasked()==MotionEvent.ACTION_CANCEL)view.getParent().requestDisallowInterceptTouchEvent(false);
                return true;
            }
        });
        int artHeight=Math.min(dp(100),(int)(getResources().getDisplayMetrics().heightPixels*.12f));
        card.addView(artwork,new LinearLayout.LayoutParams(-1,artHeight));

        source=text("PC MEDIA",10,PURPLE,true);
        source.setLetterSpacing(.09f);
        source.setSingleLine(true);
        source.setPadding(0,dp(7),0,dp(2));
        card.addView(source);
        title=text("Waiting for PC media",22,INK,true);
        title.setMinLines(2);
        title.setMaxLines(2);
        card.addView(title);
        artist=text("Start YouTube Music or another player on the PC",14,MUTED,false);
        artist.setMaxLines(1);
        artist.setPadding(0,dp(1),0,dp(4));
        card.addView(artist);

        timeline=new ChapterSeekBar(this);
        timeline.setMax(1000);
        timeline.setProgressTintList(android.content.res.ColorStateList.valueOf(PURPLE));
        timeline.setThumbTintList(android.content.res.ColorStateList.valueOf(PURPLE));
        timeline.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener(){
            public void onProgressChanged(SeekBar bar,int progress,boolean fromUser){if(fromUser)elapsed.setText(formatTime(durationMs*progress/1000));}
            public void onStartTrackingTouch(SeekBar bar){userSeeking=true;}
            public void onStopTrackingTouch(SeekBar bar){userSeeking=false;seekTo(durationMs*bar.getProgress()/1000);}
        });
        card.addView(timeline,new LinearLayout.LayoutParams(-1,dp(26)));
        LinearLayout times=new LinearLayout(this);
        elapsed=text("0:00",11,MUTED,false);
        remaining=text("-0:00",11,MUTED,false);
        times.addView(elapsed,new LinearLayout.LayoutParams(0,-2,1));
        scenes=text("SCENES",11,PURPLE,true);
        scenes.setGravity(Gravity.CENTER);
        scenes.setPadding(dp(8),0,dp(8),0);
        scenes.setVisibility(View.INVISIBLE);
        scenes.setOnClickListener(v->showScenes());
        scenes.setContentDescription("Open the chapter scene list");
        setIcon(scenes,DeckIcon.LIST,PURPLE,12,false);
        times.addView(scenes,new LinearLayout.LayoutParams(-2,-2));
        remaining.setGravity(Gravity.END);
        times.addView(remaining,new LinearLayout.LayoutParams(0,-2,1));
        times.setPadding(dp(5),0,dp(5),dp(4));
        card.addView(times);

        LinearLayout transport=new LinearLayout(this);
        transport.setGravity(Gravity.CENTER);
        previous=iconAction("PREV","previous",12,DeckIcon.PREVIOUS,INK,21,true);
        previous.setOnClickListener(v->{if(altHeld)sendKeyCommand("arrowleft");else control("previous");});
        addWeighted(transport,previous);
        play=iconAction("PLAY","play",13,DeckIcon.PLAY,Color.BLACK,22,true);
        play.setTextColor(Color.BLACK);
        play.setBackground(round(PURPLE,20));
        addWeighted(transport,play);
        next=iconAction("NEXT","next",12,DeckIcon.NEXT,INK,21,true);
        next.setOnClickListener(v->{if(altHeld)sendKeyCommand("arrowright");else control("next");});
        addWeighted(transport,next);
        card.addView(transport,new LinearLayout.LayoutParams(-1,dp(64)));

        LinearLayout seekRow=new LinearLayout(this);
        seekRow.setGravity(Gravity.CENTER);
        seekRow.setPadding(0,dp(5),0,0);
        addWeighted(seekRow,splitSeekControl(false));
        addWeighted(seekRow,splitSeekControl(true));
        card.addView(seekRow,new LinearLayout.LayoutParams(-1,dp(44)));

        LinearLayout volumeRow=new LinearLayout(this);
        volumeRow.setGravity(Gravity.CENTER);
        volumeRow.setPadding(0,dp(5),0,0);
        addWeighted(volumeRow,iconAction("VOLUME DOWN","volumedown",10,DeckIcon.VOLUME_DOWN,INK,19,true));
        addWeighted(volumeRow,iconAction("VOLUME UP","volumeup",10,DeckIcon.VOLUME_UP,INK,19,true));
        card.addView(volumeRow,new LinearLayout.LayoutParams(-1,dp(60)));

        LinearLayout modeRow=new LinearLayout(this);
        modeRow.setGravity(Gravity.CENTER);
        modeRow.setPadding(0,dp(7),0,0);
        shuffle=iconAction("SHUFFLE","shuffle",9,DeckIcon.SHUFFLE,INK,17,true);
        addWeighted(modeRow,shuffle,.92f);
        repeat=iconAction("REPEAT","repeat",9,DeckIcon.REPEAT,INK,17,true);
        addWeighted(modeRow,repeat);
        Button stop=iconAction("STOP","stop",10,DeckIcon.STOP,Color.rgb(254,202,202),16,true);
        stop.setTextColor(Color.rgb(254,202,202));
        addWeighted(modeRow,stop);
        addWeighted(modeRow,iconAction("MUTE","mute",9,DeckIcon.MUTE,INK,16,true),.78f);
        card.addView(modeRow,new LinearLayout.LayoutParams(-1,dp(54)));

        LinearLayout utilityRow=new LinearLayout(this);
        utilityRow.setGravity(Gravity.CENTER);
        utilityRow.setPadding(0,dp(5),0,0);
        Button screenshot=iconAction("SHOT","screenshot",9,DeckIcon.CAMERA,Color.BLACK,18,true);
        screenshot.setGravity(Gravity.CENTER);
        screenshot.setTextColor(Color.BLACK);
        screenshot.setBackground(round(Color.rgb(196,181,253),20));
        screenshot.setContentDescription("Save a screenshot of the PC using NVIDIA Overlay or Windows");
        screenshot.setOnClickListener(v->takeScreenshot());
        addWeighted(utilityRow,screenshot,.28f);
        utilityRow.addView(new View(this),new LinearLayout.LayoutParams(dp(4),1));
        Button moveScreen=iconAction("MOVE SCREEN","movescreen",10,DeckIcon.MONITOR,Color.BLACK,18,true);
        moveScreen.setTextColor(Color.BLACK);
        moveScreen.setBackground(round(GOLDEN_BROWN,20));
        moveScreen.setContentDescription("Move the selected PC media window to the next monitor");
        moveScreen.setOnClickListener(v->moveScreen());
        addWeighted(utilityRow,moveScreen,.72f);
        card.addView(utilityRow,new LinearLayout.LayoutParams(-1,dp(57)));

        LinearLayout youtubeRow=new LinearLayout(this);
        youtubeRow.setGravity(Gravity.CENTER);
        youtubeRow.setPadding(0,dp(5),0,0);
        Button like=iconAction("LIKE","like",9,DeckIcon.THUMB_UP,Color.BLACK,15,true);
        like.setTextColor(Color.BLACK);
        like.setBackground(round(Color.rgb(134,239,172),18));
        like.setContentDescription("Like or unlike the selected YouTube video");
        like.setOnClickListener(v->youtubeAction("like","Like"));
        addWeighted(youtubeRow,like,1.15f);
        Button dislike=iconAction("DISLIKE","dislike",8,DeckIcon.THUMB_DOWN,Color.BLACK,14,true);
        dislike.setTextColor(Color.BLACK);
        dislike.setBackground(round(Color.rgb(254,202,202),18));
        dislike.setContentDescription("Dislike or remove the dislike from the selected YouTube video");
        dislike.setOnClickListener(v->youtubeAction("dislike","Dislike"));
        addWeighted(youtubeRow,dislike,.86f);
        Button subscribe=iconAction("SUB","subscribe",9,DeckIcon.SUBSCRIBE,Color.BLACK,14,true);
        subscribe.setTextColor(Color.BLACK);
        subscribe.setBackground(round(Color.rgb(248,113,113),18));
        subscribe.setContentDescription("Subscribe to the selected YouTube channel");
        subscribe.setOnClickListener(v->youtubeAction("subscribe","Subscribe"));
        addWeighted(youtubeRow,subscribe,.72f);
        altTab=iconAction("ALT+TAB","alttab",8,DeckIcon.ALT_TAB,Color.BLACK,14,true);
        altTab.setGravity(Gravity.CENTER);
        altTab.setTextColor(Color.BLACK);
        altTab.setBackground(round(Color.rgb(251,191,36),20));
        altTab.setContentDescription("Hold Alt Tab, then tap Previous or Next with another finger to choose a PC window");
        altTab.setOnTouchListener((v,event)->{
            if(event.getActionMasked()==MotionEvent.ACTION_DOWN){v.getParent().requestDisallowInterceptTouchEvent(true);beginAltGesture();return true;}
            if(event.getActionMasked()==MotionEvent.ACTION_UP||event.getActionMasked()==MotionEvent.ACTION_CANCEL){v.getParent().requestDisallowInterceptTouchEvent(false);endAltGesture();return true;}
            return true;
        });
        addWeighted(youtubeRow,altTab,1.16f);
        card.addView(youtubeRow,new LinearLayout.LayoutParams(-1,dp(47)));

        LinearLayout youtubeVolumeRow=new LinearLayout(this);
        youtubeVolumeRow.setGravity(Gravity.CENTER_VERTICAL);
        youtubeVolumeRow.setPadding(dp(5),dp(1),dp(5),0);
        TextView youtubeVolumeLabel=text("YT VOL",9,Color.rgb(125,211,252),true);
        youtubeVolumeLabel.setGravity(Gravity.CENTER_VERTICAL);
        youtubeVolumeLabel.setSingleLine(true);
        setIcon(youtubeVolumeLabel,DeckIcon.VOLUME_UP,Color.rgb(125,211,252),12,false);
        youtubeVolumeRow.addView(youtubeVolumeLabel,new LinearLayout.LayoutParams(dp(68),-1));
        youtubeVolume=new SeekBar(this);
        youtubeVolume.setMax(100);
        youtubeVolume.setProgress(50);
        youtubeVolume.setPadding(0,0,0,0);
        youtubeVolume.setProgressTintList(android.content.res.ColorStateList.valueOf(Color.rgb(125,211,252)));
        youtubeVolume.setThumbTintList(android.content.res.ColorStateList.valueOf(Color.rgb(125,211,252)));
        youtubeVolume.setContentDescription("YouTube player volume");
        youtubeVolume.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener(){
            public void onProgressChanged(SeekBar bar,int progress,boolean fromUser){if(fromUser)youtubeVolumeValue.setText(progress+"%");}
            public void onStartTrackingTouch(SeekBar bar){youtubeVolumeSeeking=true;}
            public void onStopTrackingTouch(SeekBar bar){youtubeVolumeSeeking=false;setYoutubeVolume(bar.getProgress());}
        });
        youtubeVolumeRow.addView(youtubeVolume,new LinearLayout.LayoutParams(0,-1,1));
        youtubeVolumeValue=text("--",9,MUTED,true);
        youtubeVolumeValue.setGravity(Gravity.END|Gravity.CENTER_VERTICAL);
        youtubeVolumeRow.addView(youtubeVolumeValue,new LinearLayout.LayoutParams(dp(38),-1));
        card.addView(youtubeVolumeRow,new LinearLayout.LayoutParams(-1,dp(23)));

        LinearLayout replayRow=new LinearLayout(this);
        replayRow.setGravity(Gravity.CENTER);
        replay=new SwipeReplayView(this::handleReplayGesture);
        LinearLayout.LayoutParams replayLp=new LinearLayout.LayoutParams(0,-1,1);
        replayLp.setMargins(dp(2),0,dp(2),0);
        replayRow.addView(replay,replayLp);
        microphoneMute=new MicMuteView(this::toggleMicrophoneMute);
        LinearLayout.LayoutParams microphoneLp=new LinearLayout.LayoutParams(dp(62),-1);
        microphoneLp.setMargins(dp(2),0,dp(2),0);
        replayRow.addView(microphoneMute,microphoneLp);
        LinearLayout.LayoutParams replayRowLp=new LinearLayout.LayoutParams(-1,dp(62));
        replayRowLp.setMargins(0,dp(5),0,0);
        card.addView(replayRow,replayRowLp);
        root.addView(card);
        setContentView(root);
        root.requestApplyInsets();
    }

    private void refresh(boolean showConnecting){
        if(destroyed||io.isShutdown()||requestPending)return;
        ui.removeCallbacks(poll);
        if(deviceKey.isEmpty()){requestNearbyPairing();return;}
        requestPending=true;
        if(showConnecting){status.setText(base.isEmpty()?"FINDING PC...":"CONNECTING TO PC...");status.setTextColor(MUTED);}
        io.execute(()->{
            try{
                JSONObject data=new JSONObject(get("/api/now"));
                ui.post(()->apply(data));
            }catch(Exception first){
                if(isUnauthorized(first)){clearPairing();ui.post(this::showPairingNeeded);return;}
                try{
                    String discovered=discoverPc();
                    if(discovered!=null){
                        base=discovered;
                        getPreferences(0).edit().putString("pc",base).apply();
                        JSONObject data=new JSONObject(get("/api/now"));
                        ui.post(()->apply(data));
                        return;
                    }
                }catch(Exception second){if(isUnauthorized(second)){clearPairing();ui.post(this::showPairingNeeded);return;}}
                ui.post(this::showError);
            }
        });
    }

    private void apply(JSONObject data){
        if(destroyed)return;
        requestPending=false;
        String newTitle=data.optString("title","Nothing playing"),newArtist=data.optString("artist","Start media on your PC");
        boolean playing=data.optBoolean("playing");
        youtubeAvailable=data.optBoolean("youtubeAvailable",false);
        youtubeJumpAheadEligible=data.optBoolean("youtubeJumpAheadEligible",false);
        String sourceId=data.optString("source","PC MEDIA");
        String sourceLabel=friendlySource(sourceId);
        source.setText(sourceLabel+(youtubeAvailable?"  /  SWIPE UP FOR PICKS":""));
        title.setText(newTitle);
        artist.setText(newArtist);
        play.setText(playing?"PAUSE":"PLAY");
        setIcon(play,playing?DeckIcon.PAUSE:DeckIcon.PLAY,Color.BLACK,22,true);
        play.setOnClickListener(v->control(playing?"pause":"play"));
        durationMs=data.optLong("durationMs");
        positionMs=Math.min(data.optLong("positionMs"),durationMs);
        updateChapters(data.optJSONArray("chapters"));
        if(!userSeeking)timeline.setProgress(durationMs>0?(int)(positionMs*1000/durationMs):0);
        timeline.setChapters(chapters,durationMs);
        timeline.setEnabled(durationMs>0);
        elapsed.setText(formatTime(positionMs));
        remaining.setText("-"+formatTime(Math.max(0,durationMs-positionMs)));
        replayAvailable=data.optBoolean("instantReplayAvailable",false);
        replayEnabled=data.optBoolean("instantReplayEnabled",false);
        replaySeconds=Math.max(15,data.optInt("instantReplaySeconds",120));
        replay.setReplayState(replayAvailable,replayEnabled,replaySeconds);
        microphoneAvailable=data.optBoolean("microphoneAvailable",false);
        microphoneMuted=data.optBoolean("microphoneMuted",false);
        microphoneMute.setMicrophoneState(microphoneAvailable,microphoneMuted);
        int playerVolume=data.optInt("youtubeVolume",-1);
        youtubeVolume.setEnabled(playerVolume>=0);
        youtubeVolume.setAlpha(playerVolume>=0?1f:.35f);
        if(!youtubeVolumeSeeking){youtubeVolumeValue.setText(playerVolume>=0?playerVolume+"%":"--");if(playerVolume>=0)youtubeVolume.setProgress(playerVolume);}
        shuffle.setText(data.optBoolean("shuffle")?"SHUFFLE ON":"SHUFFLE");
        String repeatMode=data.optString("repeat","none");
        repeat.setText(repeatMode.equals("track")?"REPEAT 1":repeatMode.equals("list")?"REPEAT ALL":"REPEAT");
        status.setText("PC CONNECTED  /  SIGNED");
        status.setTextColor(PURPLE);
        String key=sourceId+'\n'+newTitle+'\n'+newArtist;
        long now=SystemClock.elapsedRealtime();
        boolean trackChanged=!key.equals(lastTrack);
        if(trackChanged){lastTrack=key;artworkLoaded=false;lastArtworkAttemptMs=0;artwork.setImageDrawable(null);}
        long retryDelay=artworkLoaded?300_000L:10_000L;
        if(!artworkPending&&(trackChanged||now-lastArtworkAttemptMs>=retryDelay)){lastArtworkAttemptMs=now;loadArtwork();}
        schedule();
    }

    private void showPairingNeeded(){requestPending=false;status.setText("ASKING PC FOR ONE-CLICK PAIRING...");status.setTextColor(Color.rgb(251,191,36));schedule();}
    private void showError(){requestPending=false;status.setText("PC OFFLINE  /  TAP PC SETTINGS");status.setTextColor(Color.rgb(248,113,113));schedule();}
    private void schedule(){if(running){ui.removeCallbacks(poll);ui.postDelayed(poll,2500);}}

    private void loadArtwork(){
        if(destroyed||io.isShutdown())return;
        artworkPending=true;
        try{io.execute(()->{
            HttpURLConnection connection=null;
            try{
                String path="/api/art?track="+System.currentTimeMillis();
                connection=openConnection(path,"GET",true);
                int code=connection.getResponseCode();
                if(code==401)throw new IOException("HTTP 401");
                if(code>=200&&code<300){
                    Bitmap bitmap=BitmapFactory.decodeStream(connection.getInputStream());
                    if(bitmap!=null){Log.i("MASHRMediaDeck","Artwork loaded "+bitmap.getWidth()+"x"+bitmap.getHeight());ui.post(()->{artworkPending=false;artworkLoaded=true;if(!isFinishing())artwork.setImageBitmap(bitmap);});return;}
                }
                Log.w("MASHRMediaDeck","Artwork request returned HTTP "+code);
            }catch(Exception error){Log.w("MASHRMediaDeck","Artwork load failed",error);}
            finally{if(connection!=null)connection.disconnect();}
            if(!destroyed)ui.post(()->{artworkPending=false;artworkLoaded=false;});
        });}catch(java.util.concurrent.RejectedExecutionException ignored){artworkPending=false;}
    }

    private void showYouTubeSuggestions(){
        if(!youtubeAvailable){Toast.makeText(this,"Open a YouTube video with the MASHR Media Deck browser helper enabled",Toast.LENGTH_LONG).show();return;}
        status.setText("LOADING YOUTUBE PICKS...");
        io.execute(()->{
            try{
                JSONArray items=new JSONArray(get("/api/youtube/suggestions"));
                ui.post(()->openSuggestionDialog(items));
            }catch(Exception error){ui.post(()->Toast.makeText(this,"Could not load YouTube suggestions",Toast.LENGTH_SHORT).show());}
        });
    }

    private void openSuggestionDialog(JSONArray items){
        if(items.length()==0){Toast.makeText(this,"No YouTube suggestions are ready yet",Toast.LENGTH_SHORT).show();return;}
        Dialog dialog=new Dialog(this);
        dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);
        LinearLayout panel=new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(12),dp(12),dp(12),dp(14));
        panel.setBackground(round(Color.rgb(17,18,27),22));
        TextView heading=text("YOUTUBE PICKS  /  TAP TO PLAY",16,INK,true);
        heading.setGravity(Gravity.CENTER);
        heading.setPadding(0,dp(2),0,dp(10));
        panel.addView(heading);
        GridLayout grid=new GridLayout(this);
        grid.setColumnCount(3);
        grid.setRowCount(3);
        for(int index=0;index<Math.min(9,items.length());index++){
            JSONObject item=items.optJSONObject(index);
            if(item==null)continue;
            String videoId=item.optString("videoId","");
            String videoTitle=item.optString("title","YouTube video");
            if(!videoId.matches("[A-Za-z0-9_-]{11}"))continue;
            LinearLayout tile=new LinearLayout(this);
            tile.setOrientation(LinearLayout.VERTICAL);
            tile.setPadding(dp(3),dp(3),dp(3),dp(4));
            tile.setBackground(round(Color.rgb(34,35,49),12));
            ImageView thumbnail=new ImageView(this);
            thumbnail.setScaleType(ImageView.ScaleType.CENTER_CROP);
            thumbnail.setBackgroundColor(Color.rgb(51,52,69));
            tile.addView(thumbnail,new LinearLayout.LayoutParams(-1,dp(62)));
            TextView label=text(videoTitle,11,INK,true);
            label.setMaxLines(2);
            label.setGravity(Gravity.CENTER);
            label.setPadding(dp(2),dp(4),dp(2),0);
            tile.addView(label,new LinearLayout.LayoutParams(-1,dp(38)));
            tile.setOnClickListener(v->{dialog.dismiss();playYouTube(videoId,videoTitle);});
            GridLayout.LayoutParams params=new GridLayout.LayoutParams();
            params.width=0;
            params.height=dp(108);
            params.columnSpec=GridLayout.spec(GridLayout.UNDEFINED,1f);
            params.setMargins(dp(3),dp(3),dp(3),dp(3));
            grid.addView(tile,params);
            loadThumbnail("https://i.ytimg.com/vi/"+videoId+"/mqdefault.jpg",thumbnail);
        }
        panel.addView(grid,new LinearLayout.LayoutParams(-1,-2));
        Button close=button("CLOSE");
        close.setOnClickListener(v->dialog.dismiss());
        LinearLayout.LayoutParams closeParams=new LinearLayout.LayoutParams(-1,dp(44));
        closeParams.setMargins(dp(3),dp(9),dp(3),0);
        panel.addView(close,closeParams);
        dialog.setContentView(panel);
        Window window=dialog.getWindow();
        if(window!=null){window.setBackgroundDrawableResource(android.R.color.transparent);window.addFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND);window.setDimAmount(.7f);window.setGravity(Gravity.BOTTOM);window.setLayout(WindowManager.LayoutParams.MATCH_PARENT,WindowManager.LayoutParams.WRAP_CONTENT);}
        dialog.setOnShowListener(ignored->{Window shown=dialog.getWindow();if(shown!=null)shown.setLayout(WindowManager.LayoutParams.MATCH_PARENT,WindowManager.LayoutParams.WRAP_CONTENT);});
        dialog.show();
    }

    private void loadThumbnail(String address,ImageView target){
        if(destroyed||thumbnails.isShutdown())return;
        try{thumbnails.execute(()->{
            HttpURLConnection connection=null;
            try{
                connection=(HttpURLConnection)new URL(address).openConnection();
                connection.setConnectTimeout(2500);
                connection.setReadTimeout(2500);
                Bitmap bitmap=BitmapFactory.decodeStream(connection.getInputStream());
                if(bitmap!=null&&!destroyed)ui.post(()->{if(!isFinishing()&&!isDestroyed())target.setImageBitmap(bitmap);});
            }catch(Exception ignored){}finally{if(connection!=null)connection.disconnect();}
        });}catch(java.util.concurrent.RejectedExecutionException ignored){}
    }

    private void playYouTube(String videoId,String videoTitle){
        io.execute(()->{
            try{post("/api/youtube/play?videoId="+videoId);ui.post(()->Toast.makeText(this,"Playing: "+videoTitle,Toast.LENGTH_SHORT).show());}
            catch(Exception error){ui.post(()->Toast.makeText(this,"That suggestion expired - swipe up again",Toast.LENGTH_SHORT).show());}
        });
    }

    private void updateChapters(JSONArray values){
        chapters.clear();
        if(values!=null){
            for(int index=0;index<Math.min(40,values.length());index++){
                JSONObject item=values.optJSONObject(index);
                if(item==null)continue;
                long marker=item.optLong("positionMs",-1);
                String label=item.optString("title","").trim();
                if(marker<0||label.isEmpty())continue;
                chapters.add(new MediaChapter(marker,label.substring(0,Math.min(120,label.length()))));
            }
        }
        scenes.setText(chapters.size()+" SCENES");
        scenes.setVisibility(chapters.size()>=2?View.VISIBLE:View.INVISIBLE);
        updateAnnotationButtons();
    }

    private MediaChapter adjacentChapter(int direction){
        if(chapters.size()<2)return null;
        final long guardMs=750;
        if(direction<0){
            MediaChapter target=null;
            for(MediaChapter chapter:chapters){if(chapter.positionMs<positionMs-guardMs)target=chapter;else break;}
            return target;
        }
        for(MediaChapter chapter:chapters)if(chapter.positionMs>positionMs+guardMs)return chapter;
        return null;
    }

    private void updateAnnotationButtons(){
        MediaChapter back=adjacentChapter(-1),ahead=adjacentChapter(1);
        boolean hasBack=back!=null,hasAhead=ahead!=null,hasAheadEdge=youtubeJumpAheadEligible||hasAhead;
        previousScene.setVisibility(hasBack?View.VISIBLE:View.GONE);
        nextScene.setVisibility(hasAheadEdge?View.VISIBLE:View.GONE);
        backSkip.setBackground(hasBack?roundSides(Color.rgb(47,44,67),18,false,true):round(Color.rgb(47,44,67),18));
        aheadSkip.setBackground(hasAheadEdge?roundSides(Color.rgb(47,44,67),18,true,false):round(Color.rgb(47,44,67),18));
        if(hasBack)previousScene.setContentDescription("Previous scene annotation: "+back.title);
        if(youtubeJumpAheadEligible){nextScene.setText("JUMP");setIcon(nextScene,DeckIcon.SEEK_FORWARD,Color.BLACK,13,false);nextScene.setContentDescription("Use YouTube Premium's Jump Ahead marker");}
        else if(hasAhead){nextScene.setText("SCENE");setIcon(nextScene,DeckIcon.NEXT,Color.BLACK,13,false);nextScene.setContentDescription("Next scene annotation: "+ahead.title);}
    }

    private void jumpSmartAhead(){
        if(!youtubeJumpAheadEligible){jumpAnnotation(1);return;}
        io.execute(()->{
            try{JSONObject result=new JSONObject(post("/api/youtube/jumpahead"));String message=result.optString("message","Skipped embedded segment");ui.post(()->Toast.makeText(this,message,Toast.LENGTH_SHORT).show());}
            catch(Exception error){ui.post(()->Toast.makeText(this,"No Premium Jump Ahead marker — using default skip",Toast.LENGTH_SHORT).show());skipBy(1);}
            ui.postDelayed(()->refresh(false),900);
        });
    }

    private void jumpAnnotation(int direction){
        MediaChapter target=adjacentChapter(direction);
        if(target==null){skipBy(direction);return;}
        seekTo(target.positionMs);
        Toast.makeText(this,(direction<0?"Previous scene: ":"Next scene: ")+target.title,Toast.LENGTH_SHORT).show();
    }

    private void showScenes(){
        if(chapters.size()<2){Toast.makeText(this,"No chapter list is published for this video",Toast.LENGTH_SHORT).show();return;}
        int active=0;
        for(int index=0;index<chapters.size();index++){if(chapters.get(index).positionMs<=positionMs)active=index;else break;}
        String[] labels=new String[chapters.size()];
        for(int index=0;index<chapters.size();index++){
            MediaChapter chapter=chapters.get(index);
            labels[index]=(index==active?"▶  ":"     ")+formatTime(chapter.positionMs)+"  "+chapter.title;
        }
        new AlertDialog.Builder(this)
            .setTitle("SCENES  /  TAP TO JUMP")
            .setItems(labels,(dialog,index)->{MediaChapter chapter=chapters.get(index);seekTo(chapter.positionMs);Toast.makeText(this,"Jumping to "+chapter.title,Toast.LENGTH_SHORT).show();})
            .setNegativeButton("CLOSE",null)
            .show();
    }

    private void handleReplayGesture(){
        if(!replayAvailable){Toast.makeText(this,"NVIDIA Instant Replay is unavailable. Check NVIDIA Overlay settings on the PC.",Toast.LENGTH_LONG).show();return;}
        final boolean save=replayEnabled;
        io.execute(()->{
            try{
                JSONObject result=new JSONObject(post(save?"/api/control/instantreplay":"/api/control/replayarm"));
                int seconds=Math.max(15,result.optInt("bufferSeconds",replaySeconds));
                boolean refocused=result.optBoolean("gameRefocused",false);
                String message=save?(refocused?"Game refocused - saving the last ":"Saving the last ")+formatTime(seconds*1000L)+" of gameplay":"Replay buffer is arming - save once the track turns green";
                ui.post(()->Toast.makeText(this,message,Toast.LENGTH_LONG).show());
            }catch(Exception error){ui.post(()->Toast.makeText(this,"Replay failed: "+apiError(error),Toast.LENGTH_LONG).show());}
            ui.postDelayed(()->refresh(false),1600);
        });
    }

    private void control(String command){io.execute(()->{try{post("/api/control/"+command);}catch(Exception ignored){}ui.postDelayed(()->refresh(false),180);});}
    private void youtubeAction(String command,String label){io.execute(()->{try{JSONObject result=new JSONObject(post("/api/control/"+command));String message=result.optString("message",label+" sent to YouTube");ui.post(()->Toast.makeText(this,message,Toast.LENGTH_SHORT).show());}catch(Exception error){ui.post(()->Toast.makeText(this,label+" unavailable: "+apiError(error),Toast.LENGTH_LONG).show());}});}
    private void setYoutubeVolume(int level){io.execute(()->{try{JSONObject result=new JSONObject(post("/api/youtube/volume?level="+level));int applied=result.optInt("volume",level);ui.post(()->youtubeVolumeValue.setText(applied+"%"));}catch(Exception error){ui.post(()->Toast.makeText(this,"YouTube volume unavailable: "+apiError(error),Toast.LENGTH_LONG).show());}});}
    private void takeScreenshot(){io.execute(()->{try{JSONObject captured=new JSONObject(post("/api/control/screenshot"));String provider=captured.optString("provider","PC");ui.post(()->Toast.makeText(this,"Screenshot saved by "+provider,Toast.LENGTH_SHORT).show());}catch(Exception error){ui.post(()->Toast.makeText(this,"Screenshot failed: "+safeMessage(error),Toast.LENGTH_LONG).show());}});}
    private void moveScreen(){io.execute(()->{try{JSONObject moved=new JSONObject(post("/api/control/movescreen"));String display=moved.optString("display","next screen");ui.post(()->Toast.makeText(this,"Screen moved to "+display,Toast.LENGTH_SHORT).show());}catch(Exception error){ui.post(()->Toast.makeText(this,"Could not move screen: "+safeMessage(error),Toast.LENGTH_LONG).show());}});}
    private void sendKeyCommand(String command){io.execute(()->{try{post("/api/control/"+command);}catch(Exception ignored){}});}
    private void beginAltGesture(){if(altHeld)return;altHeld=true;altTab.setText("ALT HELD");altTab.setBackground(round(Color.rgb(248,113,113),20));previous.setText("WINDOW");next.setText("WINDOW");setIcon(previous,DeckIcon.ARROW_LEFT,INK,21,true);setIcon(next,DeckIcon.ARROW_RIGHT,INK,21,true);sendKeyCommand("altdown");}
    private void endAltGesture(){if(!altHeld)return;altHeld=false;sendKeyCommand("altup");altTab.setText("ALT+TAB");altTab.setBackground(round(Color.rgb(251,191,36),20));previous.setText("PREV");next.setText("NEXT");setIcon(previous,DeckIcon.PREVIOUS,INK,21,true);setIcon(next,DeckIcon.NEXT,INK,21,true);}
    private void seekTo(long positionMs){io.execute(()->{try{post("/api/seek?positionMs="+positionMs);}catch(Exception ignored){}ui.postDelayed(()->refresh(false),180);});}
    private void skipBy(int direction){
        final int seconds=Math.max(1,Math.min(120,skipSeconds))*(direction<0?-1:1);
        final long fallbackPosition=Math.max(0,Math.min(durationMs,positionMs+seconds*1000L));
        io.execute(()->{
            try{post("/api/skip?seconds="+seconds);}
            catch(Exception error){
                String message=error.getMessage();
                if(message==null||!message.contains("HTTP 404"))return;
                try{if(durationMs>0)post("/api/seek?positionMs="+fallbackPosition);else post("/api/control/"+(seconds<0?"back10":"forward10"));}catch(Exception ignored){}
            }
            ui.postDelayed(()->refresh(false),180);
        });
    }

    private void toggleMicrophoneMute(){
        if(!microphoneAvailable){Toast.makeText(this,"No Windows microphone endpoint is available.",Toast.LENGTH_LONG).show();return;}
        microphoneMute.setBusy(true);
        io.execute(()->{
            try{
                JSONObject result=new JSONObject(post("/api/control/micmute"));
                boolean muted=result.optBoolean("microphoneMuted",microphoneMuted);
                microphoneMuted=muted;
                ui.post(()->{microphoneMute.setMicrophoneState(true,muted);Toast.makeText(this,muted?"PC microphone muted":"PC microphone live",Toast.LENGTH_SHORT).show();});
            }catch(Exception error){ui.post(()->Toast.makeText(this,"Microphone control failed: "+apiError(error),Toast.LENGTH_LONG).show());}
            finally{ui.post(()->microphoneMute.setBusy(false));}
            ui.postDelayed(()->refresh(false),250);
        });
    }

    private void openPcSettings(){
        LinearLayout form=new LinearLayout(this);
        form.setOrientation(LinearLayout.VERTICAL);
        form.setPadding(dp(20),dp(4),dp(20),0);
        TextView skipLabel=text("DEFAULT BACK / AHEAD SKIP",11,PURPLE,true);
        skipLabel.setPadding(0,dp(6),0,0);
        form.addView(skipLabel,new LinearLayout.LayoutParams(-1,dp(28)));
        EditText skip=new EditText(this);
        skip.setSingleLine();
        skip.setHint("Seconds (1 to 120)");
        skip.setInputType(android.text.InputType.TYPE_CLASS_NUMBER);
        skip.setText(Integer.toString(skipSeconds));
        skip.setSelectAllOnFocus(true);
        skip.setTextSize(17);
        form.addView(skip,new LinearLayout.LayoutParams(-1,dp(52)));
        EditText address=new EditText(this);
        address.setSingleLine();
        address.setHint("PC address (auto-detect if blank)");
        address.setText(base);
        address.setSelectAllOnFocus(true);
        address.setTextSize(17);
        form.addView(address,new LinearLayout.LayoutParams(-1,dp(56)));
        EditText code=new EditText(this);
        code.setSingleLine();
        code.setHint("Fallback 6-digit pairing code (optional)");
        code.setInputType(android.text.InputType.TYPE_CLASS_NUMBER);
        code.setTextSize(17);
        form.addView(code,new LinearLayout.LayoutParams(-1,dp(56)));
        new AlertDialog.Builder(this)
            .setTitle("PC SETTINGS")
            .setMessage("Normally, just open this app: the phone appears under Nearby Controllers on the PC. Confirm its name and IP, then click Pair This Device once. Use the code only if nearby discovery is unavailable.")
            .setView(form)
            .setNegativeButton("CANCEL",null)
            .setPositiveButton("CONNECT",(dialog,which)->{
                int requestedSkip;
                try{requestedSkip=Integer.parseInt(skip.getText().toString().trim());}
                catch(Exception error){Toast.makeText(this,"Skip must be from 1 to 120 seconds",Toast.LENGTH_SHORT).show();return;}
                if(requestedSkip<1||requestedSkip>120){Toast.makeText(this,"Skip must be from 1 to 120 seconds",Toast.LENGTH_SHORT).show();return;}
                skipSeconds=requestedSkip;
                getPreferences(0).edit().putInt("skipSeconds",skipSeconds).apply();
                String value=cleanAddress(address.getText().toString());
                String pairingCode=code.getText().toString().trim();
                if(!value.isEmpty()){base=value;getPreferences(0).edit().putString("pc",base).apply();}
                if(pairingCode.isEmpty()&&!deviceKey.isEmpty()){lastTrack="";refresh(true);return;}
                if(pairingCode.isEmpty()){pairRequestToken="";Toast.makeText(this,"Look for this phone in the PC dashboard",Toast.LENGTH_SHORT).show();refresh(true);return;}
                if(pairingCode.length()!=6){Toast.makeText(this,"The fallback code must be 6 digits",Toast.LENGTH_SHORT).show();return;}
                pairWithPc(pairingCode);
            }).show();
    }

    private void requestNearbyPairing(){
        if(destroyed||io.isShutdown()||requestPending)return;
        requestPending=true;
        if(pairRequestToken.isEmpty())pairRequestToken=UUID.randomUUID().toString().replace("-","");
        status.setText(base.isEmpty()?"FINDING PC FOR ONE-CLICK PAIRING...":"ASKING PC FOR ONE-CLICK PAIRING...");
        status.setTextColor(Color.rgb(251,191,36));
        io.execute(()->{
            try{
                if(nearbyPairKey==null){KeyPairGenerator generator=KeyPairGenerator.getInstance("RSA");generator.initialize(2048);nearbyPairKey=generator.generateKeyPair();}
                if(base.isEmpty()){
                    String discovered=discoverPc();
                    if(discovered==null)throw new IOException("PC not found");
                    saveDiscoveredPc(discovered);
                }
                JSONObject reply;
                try{reply=sendNearbyPairRequest();}
                catch(Exception first){
                    String discovered=discoverPc();
                    if(discovered==null)throw first;
                    saveDiscoveredPc(discovered);
                    reply=sendNearbyPairRequest();
                }
                Log.i("MASHRMediaDeck","Nearby pairing reply from "+base+": "+reply.optString("status","unknown"));
                String encrypted=reply.optString("wrappedKey","");
                if(!encrypted.isEmpty()){
                    Cipher cipher=Cipher.getInstance("RSA/ECB/OAEPWithSHA-256AndMGF1Padding");
                    OAEPParameterSpec oaep=new OAEPParameterSpec("SHA-256","MGF1",MGF1ParameterSpec.SHA256,PSource.PSpecified.DEFAULT);
                    cipher.init(Cipher.DECRYPT_MODE,nearbyPairKey.getPrivate(),oaep);
                    byte[] rawKey=cipher.doFinal(Base64.decode(encrypted,Base64.DEFAULT));
                    if(rawKey.length!=32)throw new IOException("Invalid pairing key");
                    String received=Base64.encodeToString(rawKey,Base64.NO_WRAP);
                    String assignedId=reply.optString("deviceId",deviceId);
                    if(!assignedId.isEmpty())deviceId=assignedId;
                    deviceKey=received;
                    pairRequestToken="";
                    nearbyPairKey=null;
                    getPreferences(0).edit().putString("deviceKey",deviceKey).putString("deviceId",deviceId).apply();
                    requestPending=false;
                    ui.post(()->{if(destroyed)return;Toast.makeText(this,deviceName+" paired securely",Toast.LENGTH_SHORT).show();lastTrack="";refresh(true);});
                    return;
                }
                requestPending=false;
                ui.post(()->{if(destroyed)return;status.setText("WAITING FOR PC  /  CLICK PAIR THIS DEVICE");status.setTextColor(Color.rgb(74,222,128));schedule();});
            }catch(Exception error){
                Log.w("MASHRMediaDeck","Nearby pairing request failed for "+base,error);
                requestPending=false;
                ui.post(()->{if(destroyed)return;status.setText("PC NOT FOUND  /  RETRYING ONE-CLICK PAIRING");status.setTextColor(Color.rgb(248,113,113));schedule();});
            }
        });
    }

    private JSONObject sendNearbyPairRequest()throws Exception{
        HttpURLConnection connection=openConnection("/api/pair/nearby","POST",false);
        connection.setRequestProperty("X-MediaDeck-Device",deviceId);
        connection.setRequestProperty("X-MediaDeck-Device-Name",deviceName);
        connection.setRequestProperty("X-MediaDeck-Pairing-Request",pairRequestToken);
        connection.setRequestProperty("X-MediaDeck-Pairing-Public-Key",Base64.encodeToString(nearbyPairKey.getPublic().getEncoded(),Base64.NO_WRAP));
        connection.setDoOutput(true);
        try{connection.getOutputStream().close();return new JSONObject(readResponse(connection));}
        finally{connection.disconnect();}
    }

    private void saveDiscoveredPc(String discovered){
        base=discovered;
        getPreferences(0).edit().putString("pc",base).apply();
    }

    private void pairWithPc(String code){
        requestPending=true;
        status.setText("PAIRING WITH PC...");
        status.setTextColor(Color.rgb(251,191,36));
        io.execute(()->{
            try{
                if(base.isEmpty()){
                    String discovered=discoverPc();
                    if(discovered==null)throw new IOException("PC not found");
                    base=discovered;
                    getPreferences(0).edit().putString("pc",base).apply();
                }
                HttpURLConnection connection=openConnection("/api/pair","POST",false);
                connection.setRequestProperty("X-MediaDeck-Pairing-Code",code);
                connection.setRequestProperty("X-MediaDeck-Device",deviceId);
                connection.setRequestProperty("X-MediaDeck-Device-Name",deviceName);
                connection.setDoOutput(true);
                connection.getOutputStream().close();
                String response=readResponse(connection);
                connection.disconnect();
                JSONObject paired=new JSONObject(response);
                String received=paired.optString("key","");
                if(received.isEmpty())throw new IOException("No key returned");
                if(Base64.decode(received,Base64.DEFAULT).length!=32)throw new IOException("Invalid pairing key");
                String assignedId=paired.optString("deviceId",deviceId);
                if(!assignedId.isEmpty())deviceId=assignedId;
                deviceKey=received;
                pairRequestToken="";
                getPreferences(0).edit().putString("deviceKey",deviceKey).putString("deviceId",deviceId).apply();
                requestPending=false;
                ui.post(()->{Toast.makeText(this,deviceName+" paired securely",Toast.LENGTH_SHORT).show();lastTrack="";refresh(true);});
            }catch(Exception error){requestPending=false;ui.post(()->{status.setText("PAIRING FAILED  /  CHECK THE TRAY CODE");status.setTextColor(Color.rgb(248,113,113));Toast.makeText(this,"Pairing failed: "+safeMessage(error),Toast.LENGTH_LONG).show();});}
        });
    }

    private String get(String path)throws Exception{
        HttpURLConnection connection=openConnection(path,"GET",true);
        try{return readResponse(connection);}finally{connection.disconnect();}
    }

    private String post(String path)throws Exception{
        HttpURLConnection connection=openConnection(path,"POST",true);
        connection.setDoOutput(true);
        try{connection.getOutputStream().close();return readResponse(connection);}finally{connection.disconnect();}
    }

    private HttpURLConnection openConnection(String path,String method,boolean authenticated)throws Exception{
        if(base.isEmpty())throw new IOException("No saved PC address");
        HttpURLConnection connection=(HttpURLConnection)new URL(url(path)).openConnection();
        connection.setRequestMethod(method);
        connection.setConnectTimeout(1800);
        connection.setReadTimeout(2500);
        connection.setUseCaches(false);
        if(authenticated)authorize(connection,method,path);
        return connection;
    }

    private void authorize(HttpURLConnection connection,String method,String path)throws Exception{
        if(deviceKey.isEmpty())throw new IOException("Pairing required");
        long timestamp=System.currentTimeMillis()/1000L;
        String nonce=UUID.randomUUID().toString().replace("-","");
        String canonical=method.toUpperCase(Locale.US)+"\n"+path+"\n"+timestamp+"\n"+nonce;
        Mac mac=Mac.getInstance("HmacSHA256");
        mac.init(new SecretKeySpec(Base64.decode(deviceKey,Base64.DEFAULT),"HmacSHA256"));
        String signature=Base64.encodeToString(mac.doFinal(canonical.getBytes(StandardCharsets.UTF_8)),Base64.NO_WRAP);
        connection.setRequestProperty("X-MediaDeck-Time",Long.toString(timestamp));
        connection.setRequestProperty("X-MediaDeck-Nonce",nonce);
        connection.setRequestProperty("X-MediaDeck-Signature",signature);
        connection.setRequestProperty("X-MediaDeck-Device",deviceId);
        connection.setRequestProperty("X-MediaDeck-Device-Name",deviceName);
    }

    private String readResponse(HttpURLConnection connection)throws Exception{
        int responseCode=connection.getResponseCode();
        InputStream stream=responseCode>=200&&responseCode<300?connection.getInputStream():connection.getErrorStream();
        String body=stream==null?"":readAll(stream);
        if(responseCode<200||responseCode>=300)throw new IOException("HTTP "+responseCode+(body.isEmpty()?"":" - "+body));
        return body;
    }

    private String readAll(InputStream stream)throws IOException{
        try(BufferedReader reader=new BufferedReader(new InputStreamReader(stream,StandardCharsets.UTF_8))){
            StringBuilder result=new StringBuilder();
            String line;
            while((line=reader.readLine())!=null)result.append(line);
            return result.toString();
        }
    }

    private String discoverPc(){
        try(DatagramSocket socket=new DatagramSocket(43822)){
            socket.setBroadcast(true);
            socket.setSoTimeout(1400);
            byte[] query="MEDIADECK_DISCOVER".getBytes(StandardCharsets.UTF_8);
            LinkedHashSet<InetAddress> targets=new LinkedHashSet<>();
            targets.add(InetAddress.getByName("255.255.255.255"));
            for(NetworkInterface network:Collections.list(NetworkInterface.getNetworkInterfaces())){
                if(!network.isUp()||network.isLoopback())continue;
                for(InterfaceAddress address:network.getInterfaceAddresses())if(address.getBroadcast()!=null)targets.add(address.getBroadcast());
            }
            Log.i("MASHRMediaDeck","Discovery targets: "+targets);
            for(InetAddress target:targets)socket.send(new DatagramPacket(query,query.length,target,43822));
            long deadline=SystemClock.elapsedRealtime()+1400;
            while(SystemClock.elapsedRealtime()<deadline){
                socket.setSoTimeout((int)Math.max(1,deadline-SystemClock.elapsedRealtime()));
                byte[] buffer=new byte[64];
                DatagramPacket reply=new DatagramPacket(buffer,buffer.length);
                socket.receive(reply);
                String message=new String(reply.getData(),0,reply.getLength(),StandardCharsets.UTF_8);
                if(message.equals("MEDIADECK:43821"))return reply.getAddress().getHostAddress();
            }
            return null;
        }catch(Exception error){Log.w("MASHRMediaDeck","PC discovery failed",error);return null;}
    }

    private void clearPairing(){deviceKey="";pairRequestToken="";nearbyPairKey=null;getPreferences(0).edit().remove("deviceKey").apply();}
    private String deviceDisplayName(){
        String manufacturer=Build.MANUFACTURER==null?"":Build.MANUFACTURER.trim();
        String model=Build.MODEL==null?"Android device":Build.MODEL.trim();
        String value=(manufacturer.isEmpty()?model:manufacturer+" "+model).replaceAll("[\\p{Cntrl}]","").trim();
        if(value.isEmpty())value="Android device";
        return value.length()>48?value.substring(0,48):value;
    }
    private boolean isUnauthorized(Exception error){return error.getMessage()!=null&&(error.getMessage().contains("HTTP 401")||error.getMessage().contains("HTTP 409"));}
    private String safeMessage(Exception error){String message=error.getMessage();if(message==null)return "unknown error";return message.length()>120?message.substring(0,120):message;}
    private String apiError(Exception error){String message=error.getMessage();if(message==null)return "unknown error";int jsonStart=message.indexOf('{');if(jsonStart>=0){try{String detail=new JSONObject(message.substring(jsonStart)).optString("error","");if(!detail.isEmpty())return detail;}catch(Exception ignored){}}return safeMessage(error);}
    private String cleanAddress(String value){String result=value.trim().replaceFirst("^https?://","");int slash=result.indexOf('/');if(slash>=0)result=result.substring(0,slash);int colon=result.indexOf(':');if(colon>=0)result=result.substring(0,colon);return result;}
    private String url(String path){return "http://"+base+":43821"+path;}
    private Button action(String label,String command){Button button=button(label);button.setOnClickListener(v->control(command));return button;}
    private Button largeAction(String label,String command,int size){Button button=action(label,command);button.setTextSize(size);button.setSingleLine(true);button.setPadding(dp(4),0,dp(4),0);return button;}
    private Button iconAction(String label,String command,int size,DeckIcon icon,int color,int iconSize,boolean top){Button button=largeAction(label,command,size);setIcon(button,icon,color,iconSize,top);return button;}
    private LinearLayout splitSeekControl(boolean forward){
        LinearLayout group=new LinearLayout(this);
        group.setGravity(Gravity.CENTER);
        Button skip=iconAction(forward?"AHEAD":"BACK","",11,forward?DeckIcon.SEEK_FORWARD:DeckIcon.SEEK_BACK,INK,19,false);
        skip.setPadding(dp(5),0,dp(5),0);
        skip.setContentDescription((forward?"Skip forward":"Skip backward")+" by the default amount");
        skip.setOnClickListener(v->skipBy(forward?1:-1));
        Button scene=iconAction("SCENE","",8,forward?DeckIcon.NEXT:DeckIcon.PREVIOUS,Color.BLACK,13,false);
        scene.setTextColor(Color.BLACK);
        scene.setPadding(dp(2),0,dp(2),0);
        scene.setBackground(roundSides(SCENE_BLUE,18,!forward,forward));
        scene.setVisibility(View.GONE);
        scene.setOnClickListener(v->{if(forward)jumpSmartAhead();else jumpAnnotation(-1);});
        if(forward){aheadSkip=skip;nextScene=scene;group.addView(skip,new LinearLayout.LayoutParams(0,-1,1));group.addView(scene,new LinearLayout.LayoutParams(dp(67),-1));}
        else{backSkip=skip;previousScene=scene;group.addView(scene,new LinearLayout.LayoutParams(dp(67),-1));group.addView(skip,new LinearLayout.LayoutParams(0,-1,1));}
        return group;
    }
    private void setIcon(TextView view,DeckIcon icon,int color,int size,boolean top){Drawable drawable=new DeckIconDrawable(icon,color);drawable.setBounds(0,0,dp(size),dp(size));view.setCompoundDrawablePadding(dp(top?1:4));view.setGravity(Gravity.CENTER);view.setIncludeFontPadding(false);if(top)view.setCompoundDrawables(null,drawable,null,null);else view.setCompoundDrawables(drawable,null,null,null);}
    private void addWeighted(LinearLayout row,View view){addWeighted(row,view,1f);}
    private void addWeighted(LinearLayout row,View view,float weight){LinearLayout.LayoutParams params=new LinearLayout.LayoutParams(0,-1,weight);params.setMargins(dp(2),0,dp(2),0);row.addView(view,params);}
    private String formatTime(long millis){long total=Math.max(0,millis/1000),hours=total/3600,minutes=(total%3600)/60,seconds=total%60;return hours>0?String.format(Locale.US,"%d:%02d:%02d",hours,minutes,seconds):String.format(Locale.US,"%d:%02d",minutes,seconds);}
    private String friendlySource(String value){String lower=value.toLowerCase(Locale.US);if(lower.contains("vlc"))return "VLC / PC";if(lower.contains("brave"))return "BRAVE / PC";if(lower.contains("chrome"))return "CHROME / PC";if(lower.contains("spotify"))return "SPOTIFY / PC";return "PC MEDIA";}
    private TextView text(String value,int size,int color,boolean bold){TextView view=new TextView(this);view.setText(value);view.setTextSize(size);view.setTextColor(color);view.setTypeface(Typeface.DEFAULT,bold?Typeface.BOLD:Typeface.NORMAL);view.setLineSpacing(0,1.1f);return view;}
    private Button button(String value){Button button=new Button(this);button.setText(value);button.setTextColor(INK);button.setTextSize(11);button.setTypeface(Typeface.DEFAULT_BOLD);button.setMinHeight(0);button.setMinWidth(0);button.setPadding(dp(16),0,dp(16),0);button.setBackground(round(Color.rgb(47,44,67),20));return button;}
    private GradientDrawable round(int color,int radius){GradientDrawable drawable=new GradientDrawable();drawable.setColor(color);drawable.setCornerRadius(dp(radius));return drawable;}
    private GradientDrawable roundSides(int color,int radius,boolean left,boolean right){float l=left?dp(radius):0,r=right?dp(radius):0;GradientDrawable drawable=new GradientDrawable();drawable.setColor(color);drawable.setCornerRadii(new float[]{l,l,r,r,r,r,l,l});return drawable;}
    private int dp(int value){return Math.round(value*getResources().getDisplayMetrics().density);}

    private static final class DeckIconDrawable extends Drawable {
        private final DeckIcon icon;
        private final Paint stroke=new Paint(Paint.ANTI_ALIAS_FLAG),fill=new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Path path=new Path();
        DeckIconDrawable(DeckIcon icon,int color){this.icon=icon;stroke.setColor(color);stroke.setStyle(Paint.Style.STROKE);stroke.setStrokeWidth(2.1f);stroke.setStrokeCap(Paint.Cap.ROUND);stroke.setStrokeJoin(Paint.Join.ROUND);fill.setColor(color);fill.setStyle(Paint.Style.FILL);}
        @Override public void draw(Canvas canvas){Rect b=getBounds();float scale=Math.min(b.width(),b.height())/24f;float ox=b.left+(b.width()-24*scale)/2f,oy=b.top+(b.height()-24*scale)/2f;canvas.save();canvas.translate(ox,oy);canvas.scale(scale,scale);path.reset();switch(icon){
            case SETTINGS: canvas.drawCircle(12,12,4,stroke);canvas.drawCircle(12,12,8,stroke);for(int i=0;i<8;i++){double a=i*Math.PI/4;canvas.drawLine(12+(float)Math.cos(a)*8,12+(float)Math.sin(a)*8,12+(float)Math.cos(a)*10,12+(float)Math.sin(a)*10,stroke);}break;
            case LIST: for(int y=6;y<=18;y+=6){canvas.drawCircle(4,y,1.4f,fill);canvas.drawLine(8,y,21,y,stroke);}break;
            case PREVIOUS: canvas.drawRect(4,5,6.5f,19,fill);triangle(path,18,4.5f,18,19.5f,7,12);canvas.drawPath(path,fill);break;
            case NEXT: canvas.drawRect(17.5f,5,20,19,fill);triangle(path,6,4.5f,6,19.5f,17,12);canvas.drawPath(path,fill);break;
            case PLAY: triangle(path,7,4,7,20,20,12);canvas.drawPath(path,fill);break;
            case PAUSE: canvas.drawRoundRect(new RectF(5,4,10,20),1,1,fill);canvas.drawRoundRect(new RectF(14,4,19,20),1,1,fill);break;
            case ARROW_LEFT: canvas.drawLine(20,12,5,12,stroke);canvas.drawLine(5,12,11,6,stroke);canvas.drawLine(5,12,11,18,stroke);break;
            case ARROW_RIGHT: canvas.drawLine(4,12,19,12,stroke);canvas.drawLine(19,12,13,6,stroke);canvas.drawLine(19,12,13,18,stroke);break;
            case SEEK_BACK: seek(canvas,false);break;
            case SEEK_FORWARD: seek(canvas,true);break;
            case MUTE: speaker(canvas);canvas.drawLine(16,8,22,16,stroke);canvas.drawLine(22,8,16,16,stroke);break;
            case VOLUME_DOWN: speaker(canvas);canvas.drawArc(new RectF(13,7,20,17),-48,96,false,stroke);break;
            case VOLUME_UP: speaker(canvas);canvas.drawArc(new RectF(12,7,20,17),-48,96,false,stroke);canvas.drawArc(new RectF(12,3,24,21),-48,96,false,stroke);break;
            case SHUFFLE: canvas.drawLine(3,6,7,6,stroke);canvas.drawLine(7,6,17,18,stroke);canvas.drawLine(17,18,21,18,stroke);canvas.drawLine(18,15,21,18,stroke);canvas.drawLine(18,21,21,18,stroke);canvas.drawLine(3,18,7,18,stroke);canvas.drawLine(7,18,17,6,stroke);canvas.drawLine(17,6,21,6,stroke);canvas.drawLine(18,3,21,6,stroke);canvas.drawLine(18,9,21,6,stroke);break;
            case REPEAT: canvas.drawLine(5,6,19,6,stroke);canvas.drawLine(19,6,22,9,stroke);canvas.drawLine(19,6,22,3,stroke);canvas.drawLine(19,18,5,18,stroke);canvas.drawLine(5,18,2,15,stroke);canvas.drawLine(5,18,2,21,stroke);break;
            case STOP: canvas.drawRoundRect(new RectF(5,5,19,19),2,2,fill);break;
            case CAMERA: canvas.drawRoundRect(new RectF(2,6,22,20),3,3,stroke);canvas.drawRect(8,3.5f,16,7,fill);canvas.drawCircle(12,13,4,stroke);break;
            case MONITOR: canvas.drawRoundRect(new RectF(2,3,22,17),2,2,stroke);canvas.drawLine(8,21,16,21,stroke);canvas.drawLine(12,17,12,21,stroke);canvas.drawLine(15,10,20,10,stroke);canvas.drawLine(20,10,17,7,stroke);canvas.drawLine(20,10,17,13,stroke);break;
            case THUMB_UP: thumb(canvas,false);break;
            case THUMB_DOWN: thumb(canvas,true);break;
            case SUBSCRIBE: canvas.drawCircle(8,7,3,stroke);canvas.drawArc(new RectF(3,11,13,21),180,180,false,stroke);canvas.drawLine(17,8,17,18,stroke);canvas.drawLine(12,13,22,13,stroke);break;
            case ALT_TAB: canvas.drawRoundRect(new RectF(2,4,16,15),2,2,stroke);canvas.drawRoundRect(new RectF(8,9,22,20),2,2,stroke);canvas.drawLine(5,18,11,18,stroke);canvas.drawLine(5,18,8,15,stroke);break;
        }canvas.restore();}
        private void seek(Canvas canvas,boolean forward){canvas.drawArc(new RectF(3,3,21,21),forward?-100:-80,forward?260:-260,false,stroke);if(forward){triangle(path,18,3,23,4,20,8);canvas.drawPath(path,fill);}else{triangle(path,6,3,1,4,4,8);canvas.drawPath(path,fill);}}
        private void speaker(Canvas canvas){path.moveTo(3,9);path.lineTo(8,9);path.lineTo(14,4);path.lineTo(14,20);path.lineTo(8,15);path.lineTo(3,15);path.close();canvas.drawPath(path,fill);}
        private void thumb(Canvas canvas,boolean down){canvas.save();if(down){canvas.rotate(180,12,12);}path.moveTo(4,10);path.lineTo(8,10);path.lineTo(11,4);path.quadTo(12,2,14,4);path.lineTo(14,8);path.lineTo(20,8);path.quadTo(22,8,21,11);path.lineTo(19,18);path.lineTo(8,18);path.lineTo(8,10);path.close();canvas.drawPath(path,stroke);canvas.drawLine(4,10,4,18,stroke);canvas.restore();}
        private static void triangle(Path value,float ax,float ay,float bx,float by,float cx,float cy){value.moveTo(ax,ay);value.lineTo(bx,by);value.lineTo(cx,cy);value.close();}
        @Override public void setAlpha(int alpha){stroke.setAlpha(alpha);fill.setAlpha(alpha);}
        @Override public void setColorFilter(ColorFilter filter){stroke.setColorFilter(filter);fill.setColorFilter(filter);}
        @Override public int getOpacity(){return PixelFormat.TRANSLUCENT;}
    }

    private static final class MediaChapter {
        final long positionMs;
        final String title;
        MediaChapter(long positionMs,String title){this.positionMs=positionMs;this.title=title;}
    }

    private final class ChapterSeekBar extends SeekBar {
        private final Paint marker=new Paint(Paint.ANTI_ALIAS_FLAG);
        private long[] positions=new long[0];
        private long chapterDurationMs;
        ChapterSeekBar(Activity context){super(context);marker.setColor(Color.rgb(125,211,252));}
        void setChapters(ArrayList<MediaChapter> values,long duration){
            chapterDurationMs=duration;
            positions=new long[values.size()];
            for(int index=0;index<values.size();index++)positions[index]=values.get(index).positionMs;
            setContentDescription(values.size()>=2?"Playback timeline with "+values.size()+" scene markers. Tap SCENES for the list.":"Playback timeline");
            invalidate();
        }
        @Override protected void onDraw(Canvas canvas){
            super.onDraw(canvas);
            if(chapterDurationMs<=0||positions.length<2)return;
            float left=getPaddingLeft(),width=getWidth()-getPaddingLeft()-getPaddingRight(),center=getHeight()/2f,half=getResources().getDisplayMetrics().density*1.5f,height=dp(5);
            for(long position:positions){
                if(position<=0||position>=chapterDurationMs)continue;
                float x=left+width*Math.min(1f,(float)position/chapterDurationMs);
                canvas.drawRoundRect(x-half,center-height,x+half,center+height,half,half,marker);
            }
        }
    }

    private final class MicMuteView extends View {
        private final Paint background=new Paint(Paint.ANTI_ALIAS_FLAG),microphone=new Paint(Paint.ANTI_ALIAS_FLAG),slash=new Paint(Paint.ANTI_ALIAS_FLAG),label=new Paint(Paint.ANTI_ALIAS_FLAG);
        private boolean available,muted,busy;
        MicMuteView(Runnable activate){
            super(MainActivity.this);
            setFocusable(true);
            setClickable(true);
            microphone.setStyle(Paint.Style.STROKE);
            microphone.setStrokeCap(Paint.Cap.ROUND);
            microphone.setStrokeJoin(Paint.Join.ROUND);
            microphone.setStrokeWidth(dp(2));
            slash.setStyle(Paint.Style.STROKE);
            slash.setStrokeCap(Paint.Cap.ROUND);
            slash.setStrokeWidth(dp(4));
            label.setTextAlign(Paint.Align.CENTER);
            label.setTypeface(Typeface.DEFAULT_BOLD);
            label.setTextSize(getResources().getDisplayMetrics().scaledDensity*8.5f);
            setOnClickListener(v->{if(available&&!busy){performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);activate.run();}});
            setMicrophoneState(false,false);
        }
        void setMicrophoneState(boolean available,boolean muted){
            this.available=available;
            this.muted=muted;
            setEnabled(available&&!busy);
            setAlpha(available?1f:.45f);
            setContentDescription(!available?"PC microphone unavailable":muted?"PC microphone muted. Tap to make it live.":"PC microphone live. Tap to mute.");
            invalidate();
        }
        void setBusy(boolean busy){this.busy=busy;setEnabled(available&&!busy);invalidate();}
        @Override protected void onDraw(Canvas canvas){
            super.onDraw(canvas);
            int red=Color.rgb(239,68,68);
            background.setColor(muted?red:Color.BLACK);
            microphone.setColor(muted?Color.BLACK:Color.WHITE);
            slash.setColor(muted?Color.BLACK:red);
            label.setColor(muted?Color.BLACK:Color.WHITE);
            canvas.drawRoundRect(new RectF(0,0,getWidth(),getHeight()),dp(18),dp(18),background);
            float center=getWidth()/2f,micHalf=dp(6);
            canvas.drawRoundRect(new RectF(center-micHalf,dp(8),center+micHalf,dp(29)),micHalf,micHalf,microphone);
            canvas.drawArc(new RectF(center-dp(11),dp(20),center+dp(11),dp(38)),0,180,false,microphone);
            canvas.drawLine(center,dp(38),center,dp(43),microphone);
            canvas.drawLine(center-dp(8),dp(43),center+dp(8),dp(43),microphone);
            canvas.drawLine(center-dp(13),dp(8),center+dp(13),dp(38),slash);
            canvas.drawText(busy?"...":"MIC",center,dp(56),label);
        }
    }

    private final class SwipeReplayView extends View {
        private final Paint track=new Paint(Paint.ANTI_ALIAS_FLAG),fill=new Paint(Paint.ANTI_ALIAS_FLAG),handle=new Paint(Paint.ANTI_ALIAS_FLAG),label=new Paint(Paint.ANTI_ALIAS_FLAG),replayGlyph=new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Runnable activate;
        private float progress,startY;
        private boolean tracking,complete;
        private String readyText="CHECKING GAME REPLAY...",completeText="GAME REPLAY REQUEST SENT";
        SwipeReplayView(Runnable activate){super(MainActivity.this);this.activate=activate;setFocusable(true);fill.setColor(Color.rgb(34,197,94));handle.setColor(Color.rgb(236,253,245));label.setColor(Color.WHITE);label.setTextAlign(Paint.Align.CENTER);label.setTypeface(Typeface.DEFAULT_BOLD);label.setTextSize(getResources().getDisplayMetrics().scaledDensity*14);replayGlyph.setStyle(Paint.Style.STROKE);replayGlyph.setStrokeWidth(dp(2));replayGlyph.setStrokeCap(Paint.Cap.ROUND);setReplayState(false,false,120);}
        void setReplayState(boolean available,boolean enabled,int seconds){
            track.setColor(!available?Color.rgb(69,36,36):enabled?Color.rgb(20,83,45):Color.rgb(120,53,15));
            replayGlyph.setColor(!available?Color.rgb(107,114,128):enabled?Color.rgb(20,83,45):Color.rgb(120,53,15));
            readyText=!available?"GAME REPLAY UNAVAILABLE":enabled?"RECORD LAST "+formatTime(seconds*1000L)+" OF GAME  >":"SWIPE TO ARM GAME REPLAY  >";
            completeText=enabled?"GAME CLIP REQUEST SENT":"GAME REPLAY ARM REQUEST SENT";
            setContentDescription(readyText);
            invalidate();
        }
        @Override protected void onDraw(Canvas canvas){super.onDraw(canvas);float radius=getHeight()/2f;RectF bounds=new RectF(0,0,getWidth(),getHeight());canvas.drawRoundRect(bounds,radius,radius,track);float knob=dp(22),left=knob,right=getWidth()-knob,x=left+(right-left)*progress;if(progress>0){RectF active=new RectF(0,0,x,getHeight());canvas.drawRoundRect(active,radius,radius,fill);}float center=getHeight()/2f;canvas.drawCircle(x,center,knob,handle);float glyph=dp(10);canvas.drawArc(new RectF(x-glyph,center-glyph,x+glyph,center+glyph),35,285,false,replayGlyph);Path arrow=new Path();arrow.moveTo(x+glyph*.82f,center-glyph*.25f);arrow.lineTo(x+glyph*.95f,center-glyph*.85f);arrow.lineTo(x+glyph*.35f,center-glyph*.68f);arrow.close();replayGlyph.setStyle(Paint.Style.FILL);canvas.drawPath(arrow,replayGlyph);replayGlyph.setStyle(Paint.Style.STROKE);String text=complete?completeText:tracking?"KEEP SWIPING  "+Math.round(progress*100)+"%":readyText;Paint.FontMetrics metrics=label.getFontMetrics();canvas.drawText(text,getWidth()/2f,getHeight()/2f-(metrics.ascent+metrics.descent)/2,label);}
        @Override public boolean onTouchEvent(MotionEvent event){float knob=dp(22),usable=Math.max(1,getWidth()-2*knob);switch(event.getActionMasked()){case MotionEvent.ACTION_DOWN:if(complete||event.getX()>getWidth()*.30f)return true;tracking=true;startY=event.getY();progress=Math.max(0,Math.min(1,(event.getX()-knob)/usable));getParent().requestDisallowInterceptTouchEvent(true);invalidate();return true;case MotionEvent.ACTION_MOVE:if(!tracking)return true;if(Math.abs(event.getY()-startY)>dp(42)){cancelSwipe();return true;}progress=Math.max(0,Math.min(1,(event.getX()-knob)/usable));invalidate();return true;case MotionEvent.ACTION_UP:if(tracking&&progress>=.85f){tracking=false;complete=true;progress=1;performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);activate.run();invalidate();postDelayed(()->{complete=false;progress=0;invalidate();},2200);}else cancelSwipe();getParent().requestDisallowInterceptTouchEvent(false);return true;case MotionEvent.ACTION_CANCEL:cancelSwipe();getParent().requestDisallowInterceptTouchEvent(false);return true;default:return true;}}
        private void cancelSwipe(){tracking=false;progress=0;invalidate();}
    }
}
